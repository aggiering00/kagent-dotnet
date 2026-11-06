using Grpc.Core;
using Lf.A2a.V1;
using A2ATask = Lf.A2a.V1.Task;
using Task = System.Threading.Tasks.Task;

namespace HelloWorldSandbox;

/// <summary>
/// The private A2A gRPC service (lf.a2a.v1.A2AService) that kagent's controller calls on the
/// sandbox's A2A port. Push-notification RPCs keep the generated UNIMPLEMENTED behaviour.
/// </summary>
public sealed class SandboxA2AService(AgentRunner runner, TaskStoreClient store, RuntimeConfig config) : A2AService.A2AServiceBase
{
    // Gateway attempt fence, forwarded to the TaskStore with each save.
    private const string DispatchHeader = "x-kagent-dispatch-id";

    public override async Task<SendMessageResponse> SendMessage(SendMessageRequest request, ServerCallContext context)
    {
        var run = runner.Start(RequireMessage(request), context.RequestHeaders.GetValue(DispatchHeader));
        if (request.Configuration?.ReturnImmediately == true)
        {
            var first = await Unwrap(() => run.Events.Reader.ReadAsync(context.CancellationToken).AsTask());
            return new SendMessageResponse { Task = Trim(first.Task, request.Configuration) };
        }
        var task = await Unwrap(() => run.Completion);
        return new SendMessageResponse { Task = Trim(task, request.Configuration) };
    }

    public override async Task SendStreamingMessage(SendMessageRequest request, IServerStreamWriter<StreamResponse> responseStream, ServerCallContext context)
    {
        var run = runner.Start(RequireMessage(request), context.RequestHeaders.GetValue(DispatchHeader));
        await Unwrap(async () =>
        {
            await foreach (var evt in run.Events.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(evt, context.CancellationToken);
            }
            return true;
        });
    }

    public override async Task<A2ATask> GetTask(GetTaskRequest request, ServerCallContext context)
    {
        var task = await store.GetAsync(request.Id, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"task {request.Id} not found"));
        return Trim(task, request.HasHistoryLength ? request.HistoryLength : null);
    }

    public override Task<ListTasksResponse> ListTasks(ListTasksRequest request, ServerCallContext context) =>
        store.ListAsync(request, context.CancellationToken);

    public override async Task<A2ATask> CancelTask(CancelTaskRequest request, ServerCallContext context)
    {
        if (runner.TryGetActive(request.Id, out var run))
        {
            await run.Cancellation.CancelAsync();
            return await Unwrap(() => run.Completion);
        }
        var task = await store.GetAsync(request.Id, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"task {request.Id} not found"));
        throw new RpcException(new Status(StatusCode.FailedPrecondition, $"task {request.Id} is {task.Status?.State} and cannot be canceled"));
    }

    public override async Task SubscribeToTask(SubscribeToTaskRequest request, IServerStreamWriter<StreamResponse> responseStream, ServerCallContext context)
    {
        if (!runner.TryGetActive(request.Id, out var run))
        {
            var stored = await store.GetAsync(request.Id, context.CancellationToken)
                ?? throw new RpcException(new Status(StatusCode.NotFound, $"task {request.Id} not found"));
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"task {request.Id} is {stored.Status?.State}; only active tasks can be subscribed to"));
        }
        await responseStream.WriteAsync(new StreamResponse { Task = run.Snapshot.Clone() }, context.CancellationToken);
        var final = await Unwrap(() => run.Completion);
        await responseStream.WriteAsync(new StreamResponse { Task = final }, context.CancellationToken);
    }

    public override Task<AgentCard> GetExtendedAgentCard(GetExtendedAgentCardRequest request, ServerCallContext context) =>
        Task.FromResult(config.AgentCard);

    private static Message RequireMessage(SendMessageRequest request) =>
        request.Message ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "message is required"));

    private static A2ATask Trim(A2ATask task, SendMessageConfiguration? configuration) =>
        Trim(task, configuration?.HasHistoryLength == true ? configuration.HistoryLength : null);

    private static A2ATask Trim(A2ATask task, int? historyLength)
    {
        if (historyLength is not { } length || task.History.Count <= length)
        {
            return task;
        }
        var trimmed = task.Clone();
        trimmed.History.Clear();
        trimmed.History.Add(task.History.Skip(task.History.Count - length));
        return trimmed;
    }

    // Persistence failures surface as INTERNAL with their cause rather than gRPC's opaque UNKNOWN.
    private static async Task<T> Unwrap<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"task persistence failed: {ex.Message}"));
        }
    }
}
