using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Stratara.Abstractions.Outbox;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Outbox.RabbitMQ.Outbox;

/// <summary>
/// Redis-backed <see cref="IOutboxLock"/>. Acquires a leased key under
/// <c>stratara:outbox:lock</c> using <c>SET NX</c> with the configured expiry. The instance
/// that succeeds receives a handle whose disposal removes the key via a value-checked Lua
/// script (so a stale disposal cannot release someone else's lease).
/// </summary>
/// <remarks>
/// Lease semantics: the key auto-expires after the requested lease elapses. If the holder
/// pauses (GC, disk pressure, network partition) longer than the lease, a peer may acquire
/// the lock and start a concurrent drain — the at-least-once / idempotent-consumer contract
/// of the outbox handles the resulting duplicate publish. Choose a lease that is comfortably
/// longer than the worst-case drain duration.
/// </remarks>
internal sealed class RedisOutboxLock(IConnectionMultiplexer redis, ILogger<RedisOutboxLock> logger) : IOutboxLock
{
    private const string LockKey = "stratara:outbox:lock";

    private static readonly LuaScript ReleaseScript = LuaScript.Prepare(
        "if redis.call('get', @key) == @value then return redis.call('del', @key) else return 0 end");

    /// <inheritdoc/>
    public async Task<IOutboxLockHandle?> TryAcquireAsync(TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = Guid.NewGuid().ToString("N");
        var db = redis.GetDatabase();

        try
        {
            var acquired = await db.StringSetAsync(LockKey, token, lease, When.NotExists);
            return acquired ? new Handle(db, token, logger) : null;
        }
        catch (RedisException ex)
        {
            logger.LogOutboxLockUnavailable(ex);
            return null;
        }
    }

    private sealed class Handle(IDatabase db, string token, ILogger logger) : IOutboxLockHandle
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await db.ScriptEvaluateAsync(ReleaseScript, new { key = (RedisKey)LockKey, value = token });
            }
            catch (RedisException ex)
            {
                logger.LogOutboxLockReleaseFailed(ex);
            }
        }
    }
}
