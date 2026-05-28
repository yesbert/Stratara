namespace Stratara.Mediator;

/// <summary>
/// Pre-allocated bucket-keyed lock pool used by <c>MediatorCommandWorker</c> and similar dispatchers
/// to serialise per-aggregate command processing. Pre-allocates one <see cref="SemaphoreSlim"/> per
/// bucket at construction so the steady-state acquire path is a single indexed read with no
/// allocations or dictionary lookup. The pool itself is reusable; consumers acquire via
/// <see cref="AcquireAsync"/> and dispose the returned releaser inside the same scope.
/// </summary>
internal sealed class BucketLockPool : IDisposable
{
    // Must mirror BucketConstants.TotalBucketCount in Stratara.Shared. Duplicated rather than
    // ProjectReference'd to keep the Tier-B Mediator package off the Tier-B Shared package.
    private const int TotalBucketCount = 4096;

    private readonly SemaphoreSlim[] _locks;

    public BucketLockPool()
    {
        _locks = new SemaphoreSlim[TotalBucketCount];
        for (var i = 0; i < _locks.Length; i++)
        {
            _locks[i] = new SemaphoreSlim(1, 1);
        }
    }

    public async ValueTask<IDisposable> AcquireAsync(int bucketId, CancellationToken cancellationToken)
    {
        var semaphore = _locks[bucketId];
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

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
