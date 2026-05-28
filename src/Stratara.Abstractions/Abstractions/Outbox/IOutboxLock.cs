namespace Stratara.Abstractions.Outbox;

/// <summary>
/// Mutual-exclusion primitive that coordinates outbox-drain access across multiple
/// host instances. The outbox worker calls <see cref="TryAcquireAsync"/> at the start
/// of every polling cycle; only the instance that receives a non-<see langword="null"/>
/// handle proceeds to drain. Other instances skip the cycle and re-attempt at the next
/// poll interval.
/// </summary>
/// <remarks>
/// The default implementation registered by <c>AddOutboxWorker</c> is a no-op that always
/// grants the lock, preserving the historical single-instance assumption. Consumers that
/// run more than one outbox-worker replica opt in to a real distributed lock (for example
/// via <c>AddRedisOutboxLock</c>), which acquires a leased key in shared infrastructure
/// (Redis) and auto-releases after the configured lease expires.
/// </remarks>
public interface IOutboxLock
{
    /// <summary>
    /// Attempts to acquire the outbox-drain lock with the requested <paramref name="lease"/>.
    /// </summary>
    /// <param name="lease">
    /// Maximum time the lock is held before the underlying store auto-releases it. Should be
    /// at least as long as the worst-case drain duration; otherwise the lock may expire
    /// mid-cycle and a peer can start a concurrent drain.
    /// </param>
    /// <param name="cancellationToken">Propagated to the underlying lock store.</param>
    /// <returns>
    /// A disposable handle when the lock was granted; <see langword="null"/> when another
    /// instance currently holds the lock. The caller MUST dispose the handle when the
    /// critical section completes so the lock is released early; failure to do so leaves
    /// the lock in place until the lease elapses.
    /// </returns>
    Task<IOutboxLockHandle?> TryAcquireAsync(TimeSpan lease, CancellationToken cancellationToken = default);
}

/// <summary>
/// Disposable handle returned by <see cref="IOutboxLock.TryAcquireAsync"/>. Disposing the
/// handle releases the lock; if disposal fails or never runs, the lock is auto-released
/// once the lease expires in the underlying store.
/// </summary>
public interface IOutboxLockHandle : IAsyncDisposable;
