using Stratara.Mediator;
using Stratara.Outbox.RabbitMQ.Mediator;

namespace Stratara.Infrastructure.Tests.Mediator;

public class BucketLockPoolTests
{
    [Fact]
    public async Task AcquireAsync_FirstTime_AcquiresImmediately()
    {
        var pool = new BucketLockPool();

        using var releaser = await pool.AcquireAsync(42, TestContext.Current.CancellationToken);

        Assert.NotNull(releaser);
    }

    [Fact]
    public async Task AcquireAsync_SameBucket_SecondBlocksUntilFirstReleased()
    {
        var pool = new BucketLockPool();
        var firstReleaser = await pool.AcquireAsync(42, TestContext.Current.CancellationToken);

        var secondAcquire = pool.AcquireAsync(42, TestContext.Current.CancellationToken).AsTask();

        var completedBeforeRelease = await Task.WhenAny(secondAcquire, Task.Delay(100, TestContext.Current.CancellationToken)) == secondAcquire;
        Assert.False(completedBeforeRelease, "Second acquire must block while first holds the lock.");

        firstReleaser.Dispose();

        using var secondReleaser = await secondAcquire;
        Assert.NotNull(secondReleaser);
    }

    [Fact]
    public async Task AcquireAsync_DifferentBuckets_BothAcquireImmediately()
    {
        var pool = new BucketLockPool();

        using var releaserA = await pool.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var releaserB = await pool.AcquireAsync(2, TestContext.Current.CancellationToken);

        Assert.NotNull(releaserA);
        Assert.NotNull(releaserB);
    }

    [Fact]
    public async Task Releaser_Dispose_IsIdempotent()
    {
        var pool = new BucketLockPool();
        var releaser = await pool.AcquireAsync(1, TestContext.Current.CancellationToken);

        releaser.Dispose();
        releaser.Dispose();

        // After two disposes, the lock must still be acquirable exactly once (no over-release leaving SemaphoreSlim count > 1).
        using var nextAcquire = await pool.AcquireAsync(1, TestContext.Current.CancellationToken);

        var concurrentAttempt = pool.AcquireAsync(1, TestContext.Current.CancellationToken).AsTask();
        var completed = await Task.WhenAny(concurrentAttempt, Task.Delay(100, TestContext.Current.CancellationToken)) == concurrentAttempt;
        Assert.False(completed, "Idempotent dispose must not leak permits.");

        nextAcquire.Dispose();
        using var finalReleaser = await concurrentAttempt;
        Assert.NotNull(finalReleaser);
    }

    [Fact]
    public async Task AcquireAsync_AfterRelease_ReacquiresImmediately()
    {
        var pool = new BucketLockPool();
        var first = await pool.AcquireAsync(1, TestContext.Current.CancellationToken);
        first.Dispose();

        using var second = await pool.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.NotNull(second);
    }
}
