using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Abstractions.Outbox;

namespace Stratara.Infrastructure.Tests.Outbox;

public class NullOutboxLockTests
{
    [Fact]
    public async Task TryAcquireAsync_AlwaysReturnsHandle()
    {
        IOutboxLock sut = new NullOutboxLock();

        var first = await sut.TryAcquireAsync(TimeSpan.FromSeconds(60));
        var second = await sut.TryAcquireAsync(TimeSpan.FromSeconds(60));

        Assert.NotNull(first);
        Assert.NotNull(second);

        await first!.DisposeAsync();
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task Handle_DisposeAsync_DoesNotThrow()
    {
        IOutboxLock sut = new NullOutboxLock();

        var handle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(60));

        Assert.NotNull(handle);
        var exception = await Record.ExceptionAsync(async () => await handle!.DisposeAsync());
        Assert.Null(exception);
    }
}
