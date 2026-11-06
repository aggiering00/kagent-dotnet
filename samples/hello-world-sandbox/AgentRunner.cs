using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Lf.A2a.V1;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using A2ATask = Lf.A2a.V1.Task;
using Task = System.Threading.Tasks.Task;
using TaskStatus = Lf.A2a.V1.TaskStatus;

namespace HelloWorldSandbox;

/// <summary>One task executing in this sandbox.</summary>
public sealed class ActiveRun
{
    public required string TaskId { get; init; }
    public required string ContextId { get; init; }

    /// <summary>Persisted events, in order, for the call that started the run.</summary>
    public Channel<StreamResponse> Events { get; } = Channel.CreateUnbounded<StreamResponse>();
    public CancellationTokenSource Cancellation { get; } = new();
    public Task<A2ATask> Completion { get; set; } = null!;

    /// <summary>The most recently persisted task state.</summary>
    public A2ATask Snapshot { get; set; } = new();
}

/// <summary>
/// Runs the MAF agent for A2A messages and persists every task transition to the kagent
/// TaskStore before emitting it, as kagent's own runtimes do. A sandbox holds one session,
/// so only one task may execute at a time.
/// </summary>
public sealed class AgentRunner(AIAgent agent, TaskStoreClient store, ILogger<AgentRunner> logger)
{
    // Temporary task-timeline ordering key that kagent's runtimes stamp on emitted outputs.
    private const string TimelinePositionKey = "kagent.dev/a2a/timeline-position";

    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveRun> _active = new();

    public bool TryGetActive(string taskId, out ActiveRun run) => _active.TryGetValue(taskId, out run!);

    public ActiveRun Start(Message message, string? dispatchId)
    {
        if (!string.IsNullOrEmpty(message.TaskId))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"task {message.TaskId} cannot be continued; this agent never waits for input, so send a new message without task_id"));
        }
        var userText = TextOf(message.Parts);
        if (string.IsNullOrWhiteSpace(userText))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "message must contain a text part"));
        }
        if (!_slot.Wait(0))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "session already has active work"));
        }

        var run = new ActiveRun
        {
            TaskId = Guid.CreateVersion7().ToString(),
            ContextId = string.IsNullOrEmpty(message.ContextId) ? Guid.CreateVersion7().ToString() : message.ContextId,
        };
        _active[run.TaskId] = run;

        var slotReleased = 0;
        void ReleaseSlot()
        {
            if (Interlocked.Exchange(ref slotReleased, 1) == 0)
            {
                _active.TryRemove(run.TaskId, out _);
                _slot.Release();
            }
        }

        // Runs independently of the caller's connection so a disconnect cannot strand a WORKING task.
        run.Completion = Task.Run(async () =>
        {
            try
            {
                return await ExecuteAsync(run, message, userText, dispatchId, ReleaseSlot);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Task {TaskId} could not be persisted", run.TaskId);
                run.Events.Writer.TryComplete(ex);
                throw;
            }
            finally
            {
                ReleaseSlot();
            }
        });
        return run;
    }

    private async Task<A2ATask> ExecuteAsync(ActiveRun run, Message message, string userText, string? dispatchId, Action releaseSlot)
    {
        message.TaskId = run.TaskId;
        message.ContextId = run.ContextId;
        if (message.Role == Role.Unspecified)
        {
            message.Role = Role.User;
        }

        // Persist the task before any model call so lifecycle operations can see the active work.
        var task = new A2ATask { Id = run.TaskId, ContextId = run.ContextId, Status = NewStatus(TaskState.Submitted) };
        task.History.Add(message);
        var version = await store.CreateAsync(task, dispatchId, CancellationToken.None);
        Publish(run, task, new StreamResponse { Task = task.Clone() });

        task.Status = NewStatus(TaskState.Working);
        var working = StatusEvent(task);
        version = await store.UpdateAsync(task, working, version, dispatchId, CancellationToken.None);
        Publish(run, task, working);

        string? reply = null;
        Exception? agentError = null;
        try
        {
            var messages = await LoadHistoryAsync(run.ContextId, run.TaskId, run.Cancellation.Token);
            messages.Add(new ChatMessage(ChatRole.User, userText));
            var response = await agent.RunAsync(messages, cancellationToken: run.Cancellation.Token);
            reply = response.Text;
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Surface the real cause in the task instead of a generic failure.
            logger.LogError(ex, "Agent execution failed for task {TaskId}", run.TaskId);
            agentError = ex;
        }

        if (reply != null)
        {
            var artifact = new Artifact { ArtifactId = Guid.CreateVersion7().ToString(), Name = "response", Metadata = Timeline() };
            artifact.Parts.Add(new Part { Text = reply });
            task.Artifacts.Add(artifact);
            var artifactEvent = new StreamResponse
            {
                ArtifactUpdate = new TaskArtifactUpdateEvent
                {
                    TaskId = task.Id,
                    ContextId = task.ContextId,
                    Artifact = artifact.Clone(),
                    LastChunk = true,
                },
            };
            version = await store.UpdateAsync(task, artifactEvent, version, dispatchId, CancellationToken.None);
            Publish(run, task, artifactEvent);
        }

        task.Status = run.Cancellation.IsCancellationRequested && reply == null
            ? NewStatus(TaskState.Canceled)
            : agentError != null
                ? NewStatus(TaskState.Failed, AgentMessage(task, $"Agent execution failed: {agentError.Message}"))
                : NewStatus(TaskState.Completed);
        var boundary = StatusEvent(task);
        version = await store.UpdateAsync(task, boundary, version, dispatchId, CancellationToken.None);
        // Settling acknowledges the boundary so the API publishes it and may checkpoint the sandbox.
        await store.SettleAsync(task.Id, version, CancellationToken.None);

        // Free the slot before the client sees the boundary, so it can send its next message immediately.
        releaseSlot();
        Publish(run, task, boundary);
        run.Events.Writer.TryComplete();
        logger.LogInformation("Task {TaskId} finished as {State}", task.Id, task.Status.State);
        return task;
    }

    /// <summary>Rebuilds the conversation from this context's completed tasks; the sandbox keeps no local history.</summary>
    private async Task<List<ChatMessage>> LoadHistoryAsync(string contextId, string currentTaskId, CancellationToken ct)
    {
        var messages = new List<ChatMessage>();
        try
        {
            var response = await store.ListAsync(
                new ListTasksRequest { ContextId = contextId, PageSize = 100, HistoryLength = 100, IncludeArtifacts = true }, ct);
            var previous = response.Tasks
                .Where(t => t.Id != currentTaskId && t.Status?.State == TaskState.Completed)
                .OrderBy(t => t.Status.Timestamp?.ToDateTime() ?? DateTime.MinValue);
            foreach (var previousTask in previous)
            {
                foreach (var userMessage in previousTask.History.Where(m => m.Role == Role.User))
                {
                    messages.Add(new ChatMessage(ChatRole.User, TextOf(userMessage.Parts)));
                }
                foreach (var artifact in previousTask.Artifacts)
                {
                    messages.Add(new ChatMessage(ChatRole.Assistant, TextOf(artifact.Parts)));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load history for context {ContextId}; continuing without it", contextId);
        }
        return messages;
    }

    private static void Publish(ActiveRun run, A2ATask task, StreamResponse evt)
    {
        run.Snapshot = task.Clone();
        run.Events.Writer.TryWrite(evt);
    }

    private static StreamResponse StatusEvent(A2ATask task) => new()
    {
        StatusUpdate = new TaskStatusUpdateEvent { TaskId = task.Id, ContextId = task.ContextId, Status = task.Status.Clone() },
    };

    private static TaskStatus NewStatus(TaskState state, Message? message = null) => new()
    {
        State = state,
        Message = message,
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
    };

    private static Message AgentMessage(A2ATask task, string text)
    {
        var message = new Message
        {
            MessageId = Guid.CreateVersion7().ToString(),
            TaskId = task.Id,
            ContextId = task.ContextId,
            Role = Role.Agent,
            Metadata = Timeline(),
        };
        message.Parts.Add(new Part { Text = text });
        return message;
    }

    private static Struct Timeline() => new()
    {
        Fields = { [TimelinePositionKey] = Value.ForString(DateTime.UtcNow.ToString("O")) },
    };

    internal static string TextOf(IEnumerable<Part> parts) =>
        string.Join("\n", parts.Where(p => p.ContentCase == Part.ContentOneofCase.Text).Select(p => p.Text));
}
