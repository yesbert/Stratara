using Stratara.Infrastructure.BackgroundTasks;
using Stratara.Abstractions.BackgroundTasks;

namespace Stratara.Infrastructure.Tests.BackgroundTasks;

public class BackgroundTaskQueueTests
{
    [Fact]
    public async Task Ctor_WithZeroMaxRetained_ThrowsArgumentOutOfRange()
    {
        await Task.CompletedTask;
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackgroundTaskQueue(capacity: 10, maxRetainedTaskInfos: 0));
    }

    [Fact]
    public async Task QueueTaskAsync_BeyondMaxRetained_EvictsOldestTaskInfo()
    {
        // Round-3-Audit Finding R3-Sec-006: without the cap _taskInfos grew without bound,
        // a long-running host or an authenticated spam loop could OOM the process.
        var queue = new BackgroundTaskQueue(capacity: 100, maxRetainedTaskInfos: 3);

        var first = await queue.QueueTaskAsync((_, _) => ValueTask.CompletedTask);
        var second = await queue.QueueTaskAsync((_, _) => ValueTask.CompletedTask);
        var third = await queue.QueueTaskAsync((_, _) => ValueTask.CompletedTask);
        var fourth = await queue.QueueTaskAsync((_, _) => ValueTask.CompletedTask);

        Assert.Null(queue.GetTaskInfo(first));        // oldest evicted
        Assert.NotNull(queue.GetTaskInfo(second));
        Assert.NotNull(queue.GetTaskInfo(third));
        Assert.NotNull(queue.GetTaskInfo(fourth));
    }

    [Fact]
    public async Task QueueTaskAsync_BelowMaxRetained_KeepsAllTaskInfos()
    {
        var queue = new BackgroundTaskQueue(capacity: 100, maxRetainedTaskInfos: 5);

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await queue.QueueTaskAsync((_, _) => ValueTask.CompletedTask));
        }

        foreach (var id in ids)
        {
            Assert.NotNull(queue.GetTaskInfo(id));
        }
    }

    [Fact]
    public async Task QueueTaskAsync_ReturnsTaskId()
    {
        var queue = new BackgroundTaskQueue(10);

        var taskId = await queue.QueueTaskAsync((_, _) => ValueTask.CompletedTask);

        Assert.NotEqual(Guid.Empty, taskId);
    }

    [Fact]
    public async Task QueueTaskAsync_NullTask_ThrowsArgumentNullException()
    {
        var queue = new BackgroundTaskQueue(10);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => queue.QueueTaskAsync(null!).AsTask());
    }

    [Fact]
    public async Task DequeueAsync_ReturnsQueuedTask()
    {
        var queue = new BackgroundTaskQueue(10);
        var executed = false;

        var taskId = await queue.QueueTaskAsync((_, _) =>
        {
            executed = true;
            return ValueTask.CompletedTask;
        });

        var (dequeuedId, workItem) = await queue.DequeueAsync(CancellationToken.None);
        await workItem(null!, CancellationToken.None);

        Assert.Equal(taskId, dequeuedId);
        Assert.True(executed);
    }

    [Fact]
    public async Task GetTaskInfo_ReturnsInfoForQueuedTask()
    {
        var queue = new BackgroundTaskQueue(10);

        var taskId = await queue.QueueTaskAsync((_, _) => ValueTask.CompletedTask);
        var info = queue.GetTaskInfo(taskId);

        Assert.NotNull(info);
        Assert.Equal(taskId, info.Id);
        Assert.Equal(BackgroundTaskStatus.Queued, info.Status);
    }

    [Fact]
    public void GetTaskInfo_UnknownTaskId_ReturnsNull()
    {
        var queue = new BackgroundTaskQueue(10);

        var info = queue.GetTaskInfo(Guid.NewGuid());

        Assert.Null(info);
    }

    [Fact]
    public async Task UpdateTaskStatus_UpdatesStatus()
    {
        var queue = new BackgroundTaskQueue(10);
        var taskId = await queue.QueueTaskAsync((_, _) => ValueTask.CompletedTask);

        queue.UpdateTaskStatus(taskId, BackgroundTaskStatus.Running);
        var info = queue.GetTaskInfo(taskId);

        Assert.NotNull(info);
        Assert.Equal(BackgroundTaskStatus.Running, info.Status);
    }

    [Fact]
    public async Task UpdateTaskStatus_WithError_SetsErrorMessage()
    {
        var queue = new BackgroundTaskQueue(10);
        var taskId = await queue.QueueTaskAsync((_, _) => ValueTask.CompletedTask);

        queue.UpdateTaskStatus(taskId, BackgroundTaskStatus.Failed, "Something went wrong");
        var info = queue.GetTaskInfo(taskId);

        Assert.NotNull(info);
        Assert.Equal(BackgroundTaskStatus.Failed, info.Status);
        Assert.Equal("Something went wrong", info.Error);
    }

    [Fact]
    public void UpdateTaskStatus_UnknownTaskId_DoesNotThrow()
    {
        var queue = new BackgroundTaskQueue(10);

        var exception = Record.Exception(() =>
            queue.UpdateTaskStatus(Guid.NewGuid(), BackgroundTaskStatus.Completed));

        Assert.Null(exception);
    }

    [Fact]
    public async Task QueueAndDequeue_MultipleItems_PreservesOrder()
    {
        var queue = new BackgroundTaskQueue(10);
        var order = new List<int>();

        await queue.QueueTaskAsync((_, _) => { order.Add(1); return ValueTask.CompletedTask; });
        await queue.QueueTaskAsync((_, _) => { order.Add(2); return ValueTask.CompletedTask; });
        await queue.QueueTaskAsync((_, _) => { order.Add(3); return ValueTask.CompletedTask; });

        for (var i = 0; i < 3; i++)
        {
            var (_, workItem) = await queue.DequeueAsync(CancellationToken.None);
            await workItem(null!, CancellationToken.None);
        }

        Assert.Equal([1, 2, 3], order);
    }
}
