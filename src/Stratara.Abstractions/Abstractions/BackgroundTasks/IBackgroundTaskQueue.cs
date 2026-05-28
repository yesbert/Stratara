using Stratara.Abstractions.BackgroundTasks;

namespace Stratara.Abstractions.BackgroundTasks;

/// <summary>
/// In-process channel for ad-hoc fire-and-forget work that must outlive the request scope.
/// Consumed by a hosted-service worker; one queue per host.
/// </summary>
/// <remarks>
/// Use for non-critical background tasks (e.g. cleanup, async indexing). For inter-process
/// or durable messaging use <see cref="Stratara.Abstractions.Outbox.ICommandOutboxDispatcher"/>
/// or <see cref="Stratara.Abstractions.Messaging.IMessageBus"/> instead.
/// </remarks>
public interface IBackgroundTaskQueue
{
    /// <summary>
    /// Enqueue an asynchronous unit of work. The provided delegate receives a fresh DI
    /// scope at execution time.
    /// </summary>
    /// <param name="task">The async delegate to execute.</param>
    /// <returns>The id of the queued task — usable with <see cref="GetTaskInfo"/>.</returns>
    ValueTask<Guid> QueueTaskAsync(Func<IServiceProvider, CancellationToken, ValueTask> task);

    /// <summary>
    /// Dequeue the next task. Blocks asynchronously until one is available or
    /// <paramref name="cancellationToken"/> is signalled. Called by the consumer worker.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait when the host is shutting down.</param>
    /// <returns>The task id paired with its delegate.</returns>
    ValueTask<(Guid taskId, Func<IServiceProvider, CancellationToken, ValueTask> task)> DequeueAsync(
        CancellationToken cancellationToken);

    /// <summary>Look up status + error info for a previously-enqueued task.</summary>
    /// <param name="taskId">The id returned from <see cref="QueueTaskAsync"/>.</param>
    /// <returns>The current info, or <c>null</c> if the task is unknown / already evicted.</returns>
    BackgroundTaskInfo? GetTaskInfo(Guid taskId);

    /// <summary>Update the recorded status of a task — called by the consumer worker.</summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="status">The new status.</param>
    /// <param name="error">Optional error message; required only for <see cref="BackgroundTaskStatus.Failed"/>.</param>
    void UpdateTaskStatus(Guid taskId, BackgroundTaskStatus status, string? error = null);
}
