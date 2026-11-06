using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;
using Stratara.Abstractions.Outbox;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Outbox;

[Collection(RedisCollection.Name)]
public class RedisOutboxLockTests(RedisFixture redis)
{
    private const string LockKey = "stratara:outbox:lock";

    private static RedisOutboxLock CreateSut(IConnectionMultiplexer connection, string? productName = null)
        => new(connection, Options.Create(new RedisCoordinationOptions { ProductName = productName }), NullLogger<RedisOutboxLock>.Instance);

    [Fact]
    public async Task TryAcquireAsync_OnEmptyLockKey_ReturnsHandle()
    {
        await redis.FlushAsync();
        var sut = CreateSut(redis.Connection);

        await using var handle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(handle);
        Assert.True(await redis.Connection.GetDatabase().KeyExistsAsync(LockKey));
    }

    [Fact]
    public async Task TryAcquireAsync_WhilePeerHolds_ReturnsNull()
    {
        await redis.FlushAsync();
        var sut = CreateSut(redis.Connection);

        await using var firstHandle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));
        var secondHandle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(firstHandle);
        Assert.Null(secondHandle);
    }

    [Fact]
    public async Task Handle_DisposeAsync_ReleasesLockSoSubsequentAcquireSucceeds()
    {
        await redis.FlushAsync();
        var sut = CreateSut(redis.Connection);

        var firstHandle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(firstHandle);
        await firstHandle.DisposeAsync();

        await using var secondHandle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(secondHandle);
        Assert.True(await redis.Connection.GetDatabase().KeyExistsAsync(LockKey));
    }

    [Fact]
    public async Task LeaseExpiry_ReleasesLockEvenWithoutExplicitDispose()
    {
        await redis.FlushAsync();
        var sut = CreateSut(redis.Connection);

        var firstHandle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(firstHandle);

        await Task.Delay(TimeSpan.FromSeconds(2));

        await using var secondHandle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(secondHandle);
    }

    [Fact]
    public async Task LateDisposeAfterLeaseExpiry_DoesNotReleasePeerLock()
    {
        await redis.FlushAsync();
        var sut = CreateSut(redis.Connection);
        var db = redis.Connection.GetDatabase();

        // A acquires with a short lease and we let it expire.
        var staleHandle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(staleHandle);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(await db.KeyExistsAsync(LockKey));

        // B acquires the now-free lock.
        await using var peerHandle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(peerHandle);
        var peerToken = await db.StringGetAsync(LockKey);
        Assert.False(peerToken.IsNullOrEmpty);

        // A's late dispose must not delete B's key (Lua CAS protects against stale releases).
        await staleHandle.DisposeAsync();

        var tokenAfterStaleDispose = await db.StringGetAsync(LockKey);
        Assert.Equal(peerToken, tokenAfterStaleDispose);

        // A peer trying to acquire while B still holds must still see the lock as taken.
        var contended = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));
        Assert.Null(contended);
    }

    [Fact]
    public async Task TryAcquireAsync_ReturnsNullWhenRedisIsUnreachable()
    {
        // Connect with abortConnect=false against a port nothing is listening on; the
        // returned multiplexer is constructible but every command throws RedisException.
        var options = ConfigurationOptions.Parse("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200");
        await using var dead = await ConnectionMultiplexer.ConnectAsync(options);
        var sut = CreateSut(dead);

        var handle = await sut.TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.Null(handle);
    }

    [Fact]
    public async Task TryAcquireAsync_HonoursPreCancelledToken()
    {
        await redis.FlushAsync();
        var sut = CreateSut(redis.Connection);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.TryAcquireAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task TwoProducts_EachAcquireTheirOwnLock()
    {
        await redis.FlushAsync();

        await using var first = await CreateSut(redis.Connection, "product-a").TryAcquireAsync(TimeSpan.FromSeconds(30));
        await using var second = await CreateSut(redis.Connection, "product-b").TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task TwoWorkersOfOneProduct_ExcludeEachOther_AndReleaseUnderTheirName()
    {
        await redis.FlushAsync();
        var worker = CreateSut(redis.Connection, "product-a");
        var peer = CreateSut(redis.Connection, "product-a");

        var held = await worker.TryAcquireAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(held);
        Assert.True(await redis.Connection.GetDatabase().KeyExistsAsync("stratara:product-a:outbox:lock"));
        Assert.Null(await peer.TryAcquireAsync(TimeSpan.FromSeconds(30)));

        await held.DisposeAsync();

        await using var next = await peer.TryAcquireAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task ANamedWorkerAndAnUnnamedWorker_DoNotExcludeEachOther()
    {
        await redis.FlushAsync();

        await using var unnamed = await CreateSut(redis.Connection).TryAcquireAsync(TimeSpan.FromSeconds(30));
        await using var named = await CreateSut(redis.Connection, "product-a").TryAcquireAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(unnamed);
        Assert.NotNull(named);
    }

    [Fact]
    public async Task ImplementsIOutboxLockContract()
    {
        var sut = CreateSut(redis.Connection);
        Assert.IsType<IOutboxLock>(sut, exactMatch: false);
    }
}
