using System.Collections.Concurrent;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Task 4.2 of optimise-the-orleans-execution-model: the completion queue's two bounds. A full batch
/// flushes at once, a lone id flushes when its window ends, and stopping flushes what is left.
/// </summary>
public sealed class IntentCompletionQueueTests
{
    [Fact]
    public async Task A_full_batch_flushes_at_once_and_never_more_than_the_batch_size()
    {
        var flushes = new ConcurrentQueue<int>();
        var flushed = new ConcurrentBag<Guid>();
        var queue = new IntentCompletionQueue(TimeSpan.FromSeconds(10), 64, (ids, _) =>
        {
            flushes.Enqueue(ids.Count);
            foreach (var id in ids)
            {
                flushed.Add(id);
            }

            return Task.CompletedTask;
        });
        await queue.StartAsync(CancellationToken.None);

        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
        {
            queue.Complete(id);
        }

        await WaitUntilAsync(() => flushed.Count >= 192, TimeSpan.FromSeconds(2));
        Assert.All(flushes, count => Assert.True(count <= 64, $"a flush held {count} ids"));
        Assert.True(flushes.Count(count => count == 64) >= 3, "three full batches were expected before the window ended");

        await queue.StopAsync(CancellationToken.None);
        Assert.Equal(ids.Order(), flushed.Order());
    }

    [Fact]
    public async Task A_lone_id_flushes_when_the_window_ends()
    {
        var flushedAt = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new IntentCompletionQueue(TimeSpan.FromMilliseconds(50), 64, (_, _) =>
        {
            flushedAt.TrySetResult(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });
        await queue.StartAsync(CancellationToken.None);

        var started = DateTimeOffset.UtcNow;
        queue.Complete(Guid.NewGuid());

        var at = await flushedAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.InRange((at - started).TotalMilliseconds, 20, 1000);
        await queue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_flushes_what_is_left()
    {
        var flushed = new ConcurrentBag<Guid>();
        var queue = new IntentCompletionQueue(TimeSpan.FromSeconds(10), 64, (ids, _) =>
        {
            foreach (var id in ids)
            {
                flushed.Add(id);
            }

            return Task.CompletedTask;
        });
        await queue.StartAsync(CancellationToken.None);

        var id = Guid.NewGuid();
        queue.Complete(id);
        await queue.StopAsync(CancellationToken.None);

        Assert.Contains(id, flushed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}
