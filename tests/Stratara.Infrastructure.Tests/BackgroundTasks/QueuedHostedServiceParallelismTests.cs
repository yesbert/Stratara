using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Abstractions.BackgroundTasks;
using Stratara.Infrastructure.BackgroundTasks;
using Xunit;

namespace Stratara.Infrastructure.Tests.BackgroundTasks;

/// <summary>
/// The worker runs one loop per processor. Nothing covered that: reducing it to a single loop would
/// pass the entire suite, and the only symptom would be a queue that drains more slowly than the
/// machine allows.
/// </summary>
public class QueuedHostedServiceParallelismTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task MoreThanOneItemIsInFlightAtOnce()
    {
        if (Environment.ProcessorCount < 2)
        {
            Assert.Fail(
                "This test asserts concurrency and needs at least two processors; " +
                $"this machine reports {Environment.ProcessorCount}. It is not skipped, because a " +
                "silently skipped concurrency test is how this gap arose in the first place.");
        }

        var queue = new BackgroundTaskQueue(capacity: 16);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        await using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new QueuedHostedService(NullLogger<QueuedHostedService>.Instance, provider, queue);

        for (var i = 0; i < 2; i++)
        {
            await queue.QueueTaskAsync(async (_, _) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                {
                    bothStarted.SetResult();
                }

                await release.Task;
            });
        }

        using var stopping = new CancellationTokenSource();
        await service.StartAsync(stopping.Token);

        var completed = await Task.WhenAny(bothStarted.Task, Task.Delay(Timeout, stopping.Token));
        release.SetResult();
        await stopping.CancelAsync();

        Assert.True(
            ReferenceEquals(completed, bothStarted.Task),
            $"Only {Volatile.Read(ref started)} of 2 queued items started while the first was blocked. " +
            "The worker is draining the queue with a single loop.");
    }

    [Fact]
    public async Task AFailingItemDoesNotStopTheWorker()
    {
        var queue = new BackgroundTaskQueue(capacity: 16);
        var secondRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new QueuedHostedService(NullLogger<QueuedHostedService>.Instance, provider, queue);

        var failing = await queue.QueueTaskAsync((_, _) => throw new InvalidOperationException("boom"));
        await queue.QueueTaskAsync((_, _) =>
        {
            secondRan.SetResult();
            return ValueTask.CompletedTask;
        });

        using var stopping = new CancellationTokenSource();
        await service.StartAsync(stopping.Token);

        var completed = await Task.WhenAny(secondRan.Task, Task.Delay(Timeout, stopping.Token));
        await stopping.CancelAsync();

        Assert.True(ReferenceEquals(completed, secondRan.Task), "The item after a failing one never ran.");
        Assert.Equal(BackgroundTaskStatus.Failed, queue.GetTaskInfo(failing)?.Status);
    }
}
