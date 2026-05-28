using Stratara.Abstractions.Outbox;

namespace Stratara.Outbox.RabbitMQ.Outbox;

/// <summary>
/// No-op <see cref="IOutboxLock"/> that always grants the lock. Registered as the default
/// when callers do not opt in to a distributed lock; preserves Stratara's historical
/// single-instance outbox-worker assumption.
/// </summary>
/// <remarks>
/// Use this implementation when the outbox worker runs on exactly one host. Running multiple
/// worker instances against the same database with this lock in place can cause duplicate
/// publishes — switch to a distributed lock such as <c>RedisOutboxLock</c> in that case.
/// </remarks>
internal sealed class NullOutboxLock : IOutboxLock
{
    /// <inheritdoc/>
    public Task<IOutboxLockHandle?> TryAcquireAsync(TimeSpan lease, CancellationToken cancellationToken = default)
        => Task.FromResult<IOutboxLockHandle?>(NullHandle.Instance);

    private sealed class NullHandle : IOutboxLockHandle
    {
        public static readonly NullHandle Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
