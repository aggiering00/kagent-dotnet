using Grpc.Core;
using Grpc.Net.Client;
using Kagent.Runtime.V1;
using Lf.A2a.V1;
using A2ATask = Lf.A2a.V1.Task;
using Task = System.Threading.Tasks.Task;

namespace HelloWorldSandbox;

/// <summary>
/// Client for kagent's private TaskStore (kagent.runtime.v1.TaskStoreService) at runtime.control_plane_url.
/// Ported from kagent's Go and Python runtimes (go/adk/pkg/taskstore/store.go,
/// python/packages/kagent-core/src/kagent/core/a2a/_task_store.py): every task mutation is
/// persisted here with optimistic versions, and boundary states are settled so the API publishes them.
/// </summary>
public sealed class TaskStoreClient
{
    private const string IdentityDirectory = "/run/kagent/identity";
    // Temporary identity transport until Substrate injects actor credentials (kagent #1660).
    private const string RuntimeIdentityHeader = "x-kagent-insecure-runtime-identity";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private const int MaxMessageBytes = 16 << 20;

    private readonly TaskStoreService.TaskStoreServiceClient _client;

    public TaskStoreClient(string controlPlaneUrl)
    {
        var channel = GrpcChannel.ForAddress(controlPlaneUrl, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = MaxMessageBytes,
            MaxSendMessageSize = MaxMessageBytes,
        });
        _client = new TaskStoreService.TaskStoreServiceClient(channel);
    }

    public async Task<long> CreateAsync(A2ATask task, string? dispatchId, CancellationToken ct)
    {
        var request = new TaskStoreServiceCreateTaskRequest { SessionId = await SessionIdAsync(ct), Task = task.Clone() };
        if (dispatchId != null)
        {
            request.DispatchId = dispatchId;
        }
        var response = await CallAsync((options) => _client.CreateTaskAsync(request, options).ResponseAsync, ct);
        return response.Version;
    }

    public async Task<long> UpdateAsync(A2ATask task, StreamResponse evt, long expectedVersion, string? dispatchId, CancellationToken ct)
    {
        var request = new TaskStoreServiceUpdateTaskRequest
        {
            SessionId = await SessionIdAsync(ct),
            Task = task.Clone(),
            Event = evt.Clone(),
            ExpectedVersion = expectedVersion,
        };
        if (dispatchId != null)
        {
            request.DispatchId = dispatchId;
        }
        var response = await CallAsync((options) => _client.UpdateTaskAsync(request, options).ResponseAsync, ct);
        return response.Version;
    }

    public async Task SettleAsync(string taskId, long version, CancellationToken ct)
    {
        var request = new TaskStoreServiceSettleTaskRequest { SessionId = await SessionIdAsync(ct), TaskId = taskId, Version = version };
        await CallAsync((options) => _client.SettleTaskAsync(request, options).ResponseAsync, ct);
    }

    /// <summary>Returns the stored task, or null when the store has no such task.</summary>
    public async Task<A2ATask?> GetAsync(string taskId, CancellationToken ct)
    {
        var request = new TaskStoreServiceGetTaskRequest { SessionId = await SessionIdAsync(ct), TaskId = taskId };
        try
        {
            var response = await CallAsync((options) => _client.GetTaskAsync(request, options).ResponseAsync, ct);
            return response.Stored?.Task;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ListTasksResponse> ListAsync(ListTasksRequest listRequest, CancellationToken ct)
    {
        var request = new TaskStoreServiceListTasksRequest { SessionId = await SessionIdAsync(ct), Request = listRequest };
        var response = await CallAsync((options) => _client.ListTasksAsync(request, options).ResponseAsync, ct);
        return response.Result ?? new ListTasksResponse();
    }

    // Retries only lost responses; conflicts and other errors surface to the caller unchanged.
    private static async Task<T> CallAsync<T>(Func<CallOptions, Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Reread on every call because restore rebinds the identity files to the new actor.
            var options = new CallOptions(
                headers: new Metadata { { RuntimeIdentityHeader, await RuntimeIdentityAsync(ct) } },
                deadline: DateTime.UtcNow.Add(CallTimeout),
                cancellationToken: ct);
            try
            {
                return await call(options);
            }
            catch (RpcException ex) when (attempt < 3 && ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), ct);
            }
        }
    }

    private static async Task<string> SessionIdAsync(CancellationToken ct)
    {
        var name = (await File.ReadAllTextAsync(Path.Combine(IdentityDirectory, "name"), ct)).Trim();
        if (!name.StartsWith("session-", StringComparison.Ordinal) || !Guid.TryParse(name["session-".Length..], out var id))
        {
            throw new InvalidOperationException($"unexpected runtime actor name '{name}'");
        }
        return id.ToString();
    }

    private static async Task<string> RuntimeIdentityAsync(CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var field in new[] { "atespace", "name", "uid" })
        {
            parts.Add((await File.ReadAllTextAsync(Path.Combine(IdentityDirectory, field), ct)).Trim());
        }
        return string.Join('/', parts);
    }
}
