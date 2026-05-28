using System.Collections.Concurrent;
using System.Threading.Channels;
using Stratara.Abstractions.BackgroundTasks;

namespace Stratara.Infrastructure.BackgroundTasks;

/// <summary>
/// In-process, bounded <see cref="IBackgroundTaskQueue"/> backed by a <see cref="Channel{T}"/> and
/// a concurrent dictionary that tracks <see cref="BackgroundTaskInfo"/> for queued items.
/// </summary>
/// <remarks>
/// Each queued task is assigned a fresh <see cref="Guid"/> and a <see cref="BackgroundTaskInfo"/> entry.
/// Producers wait when the channel is full (<see cref="BoundedChannelFullMode.Wait"/>) instead of
/// dropping work. The queue is process-local; cross-process scenarios should use the Outbox stack.
/// <para>
/// Since 3.0.12 the task-info dictionary applies FIFO eviction once it exceeds the configured retention
/// cap (default 10 000 entries, the same as the channel capacity for a typical host). Round-3-Audit
/// Finding R3-Sec-006: without the cap the dictionary grew without bound — every queued task added an
/// entry, nothing ever removed one. A long-running host or an authenticated spam loop could OOM the
/// process by enqueuing millions of tasks.
/// </para>
/// </remarks>
internal sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private const int DefaultMaxRetainedTaskInfos = 10_000;

    private readonly ConcurrentDictionary<Guid, BackgroundTaskInfo> _taskInfos = new();
    private readonly ConcurrentQueue<Guid> _taskInfoOrder = new();
    private readonly Channel<(Guid taskId, Func<IServiceProvider, CancellationToken, ValueTask> task)> _workItems;
    private readonly int _maxRetainedTaskInfos;

    /// <summary>Creates a new queue with the given bounded <paramref name="capacity"/>.</summary>
    /// <param name="capacity">Maximum number of pending work items before producers start waiting.</param>
    public BackgroundTaskQueue(int capacity) : this(capacity, DefaultMaxRetainedTaskInfos) { }

    /// <summary>
    /// Creates a new queue with the given channel <paramref name="capacity"/> and an explicit
    /// <paramref name="maxRetainedTaskInfos"/> cap.
    /// </summary>
    /// <param name="capacity">Maximum number of pending work items before producers start waiting.</param>
    /// <param name="maxRetainedTaskInfos">Maximum number of <see cref="BackgroundTaskInfo"/> entries kept in memory; FIFO eviction past this point. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetainedTaskInfos"/> is not positive.</exception>
    public BackgroundTaskQueue(int capacity, int maxRetainedTaskInfos)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetainedTaskInfos);

        var options = new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait };
        _workItems = Channel.CreateBounded<(Guid taskId, Func<IServiceProvider, CancellationToken, ValueTask> task)>(options);
        _maxRetainedTaskInfos = maxRetainedTaskInfos;
    }

    /// <inheritdoc/>
    public async ValueTask<Guid> QueueTaskAsync(
        Func<IServiceProvider, CancellationToken, ValueTask> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var taskInfo = new BackgroundTaskInfo();
        _taskInfos[taskInfo.Id] = taskInfo;
        _taskInfoOrder.Enqueue(taskInfo.Id);
        EvictOldestIfOverCap();
        await _workItems.Writer.WriteAsync((taskInfo.Id, task));
        return taskInfo.Id;
    }

    /// <inheritdoc/>
    public async ValueTask<(Guid taskId, Func<IServiceProvider, CancellationToken, ValueTask> task)> DequeueAsync(
        CancellationToken cancellationToken)
    {
        var workItem = await _workItems.Reader.ReadAsync(cancellationToken);
        return workItem;
    }

    /// <inheritdoc/>
    public BackgroundTaskInfo? GetTaskInfo(Guid taskId) => _taskInfos.GetValueOrDefault(taskId);

    /// <inheritdoc/>
    public void UpdateTaskStatus(Guid taskId, BackgroundTaskStatus status, string? error = null)
    {
        if (!_taskInfos.TryGetValue(taskId, out var task))
        {
            return;
        }

        task.Status = status;
        task.Error = error;
    }

    private void EvictOldestIfOverCap()
    {
        while (_taskInfos.Count > _maxRetainedTaskInfos && _taskInfoOrder.TryDequeue(out var oldestId))
        {
            _taskInfos.TryRemove(oldestId, out _);
        }
    }
}
