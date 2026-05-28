using Stratara.Outbox.RabbitMQ.Outbox;

namespace Stratara.Outbox.RabbitMQ.Tests.Outbox;

public class NullOutboxLockTests
{
    [Fact]
    public async Task TryAcquireAsync_AlwaysReturnsHandle()
    {
        var sut = new NullOutboxLock();

        var handle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(handle);
    }

    [Fact]
    public async Task TryAcquireAsync_GrantsLockEvenWhenSecondCallerAsksConcurrently()
    {
        var sut = new NullOutboxLock();

        var first = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));
        var second = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Handle_DisposeAsync_DoesNotThrow()
    {
        var sut = new NullOutboxLock();

        var handle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(handle);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task Handle_DisposeAsync_IsIdempotent()
    {
        var sut = new NullOutboxLock();

        var handle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(handle);
        await handle.DisposeAsync();
        await handle.DisposeAsync();
    }
}
