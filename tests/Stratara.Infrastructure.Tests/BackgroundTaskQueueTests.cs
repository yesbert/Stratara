using Microsoft.Extensions.DependencyInjection;
using Stratara.Infrastructure.BackgroundTasks;
using Stratara.Abstractions.BackgroundTasks;

namespace Stratara.Infrastructure.Tests;

public class BackgroundTaskQueueTests
{
    [Fact]
    public async Task QueueTaskAsync_Then_DequeueAsync_Returns_Same_Task()
    {
        // Arrange
        var queue = new BackgroundTaskQueue(10);
        var tcs = new TaskCompletionSource<bool>();

        async ValueTask Work(IServiceProvider _, CancellationToken __)
        {
            tcs.SetResult(true);
            await Task.CompletedTask;
        }

        // Act
        var id = await queue.QueueTaskAsync(Work);
        var (dequeuedId, task) = await queue.DequeueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(id, dequeuedId);
        await task.Invoke(new ServiceCollection().BuildServiceProvider(), CancellationToken.None);
        Assert.True(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task UpdateTaskStatus_Changes_Status_And_Error()
    {
        // Arrange
        var queue = new BackgroundTaskQueue(2);
        async ValueTask Work(IServiceProvider _, CancellationToken __) => await Task.CompletedTask;
        var id = await queue.QueueTaskAsync(Work);

        // Act
        queue.UpdateTaskStatus(id, BackgroundTaskStatus.Running);
        var info1 = queue.GetTaskInfo(id);

        // Assert first update
        Assert.NotNull(info1);
        Assert.Equal(BackgroundTaskStatus.Running, info1.Status);

        // Act second update
        queue.UpdateTaskStatus(id, BackgroundTaskStatus.Failed, "boom");
        var info2 = queue.GetTaskInfo(id);

        // Assert second update
        Assert.NotNull(info2);
        Assert.Equal(BackgroundTaskStatus.Failed, info2.Status);
        Assert.Equal("boom", info2.Error);
    }

    [Fact]
    public async Task DequeueAsync_Honors_Cancellation()
    {
        // Arrange
        var queue = new BackgroundTaskQueue(1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act + Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () => { await queue.DequeueAsync(cts.Token); });
    }
}