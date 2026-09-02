namespace Stratara.Abstractions.Partitioning;

/// <summary>
/// Pre-allocated pool of per-bucket locks that serialises work keyed on an aggregate identity.
/// </summary>
/// <remarks>
/// One <see cref="SemaphoreSlim"/> is allocated per bucket at construction, so the steady-state acquire
/// path is a single indexed read with no allocation and no dictionary lookup. The bucket of an identity
/// is its hash modulo <see cref="BucketCount"/>; two identities that share a bucket serialise against
/// each other, which costs throughput and never correctness. The command worker uses the pool to run
/// commands naming one aggregate one at a time, and the projection and saga workers use it to apply
/// bundles about one aggregate one at a time — each within its own process.
/// </remarks>
public sealed class BucketLockPool : IDisposable
{
    /// <summary>The number of buckets, and therefore of locks, the pool holds.</summary>
    public const int BucketCount = 4096;

    private readonly SemaphoreSlim[] _locks;

    /// <summary>Initializes a pool with one lock per bucket.</summary>
    public BucketLockPool()
    {
        _locks = new SemaphoreSlim[BucketCount];
        for (var i = 0; i < _locks.Length; i++)
        {
            _locks[i] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>
    /// Acquires the lock for <paramref name="bucketId"/>, waiting until it is free. Dispose the returned
    /// releaser to let the next waiter through; disposing it twice releases once.
    /// </summary>
    /// <param name="bucketId">The bucket to lock, in <c>[0, <see cref="BucketCount"/>)</c>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A releaser that frees the bucket when disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bucketId"/> is negative or not below <see cref="BucketCount"/>.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    public async ValueTask<IDisposable> AcquireAsync(int bucketId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bucketId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bucketId, BucketCount);

        var semaphore = _locks[bucketId];
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        for (var i = 0; i < _locks.Length; i++)
        {
            _locks[i].Dispose();
        }
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
