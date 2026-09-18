namespace Stratara.Orleans.Projections;

/// <summary>
/// Pausing a set of store readers under one pauser, and holding them paused until that pauser resumes them. A reader
/// takes the pause before it waits for the running loop, so a pause whose answer is lost — a partition whose batch
/// outlives the response timeout — has paused the reader all the same: what is resumed is therefore every reader the
/// pause was asked of, not only those that answered. A reader releases only the pauser it holds, so resuming one
/// reader too many, or one reader twice, costs nothing; a reader whose resume never arrives resumes by itself once the
/// pause's lease has passed without a renewal.
/// </summary>
internal static class StoreReaderPause
{
    /// <summary>
    /// Pauses every reader under a new pauser and returns the hold that renews the pause and resumes every reader the
    /// pause was asked of. The hold renews from the moment the pauses are sent, so a reader that answered at once does not
    /// lapse while another's pause waits for a long batch. Where a pause fails, renewing stops and the readers are resumed
    /// before this throws, so a caller that sees the exception holds nothing.
    /// </summary>
    /// <param name="readers">The readers to pause, by the name of the partition they read.</param>
    /// <param name="lease">How long a pause lasts without a renewal, and the clock the renewals run on.</param>
    /// <returns>The hold over the readers.</returns>
    /// <exception cref="InvalidOperationException">A reader could not be paused; the message names which.</exception>
    public static async ValueTask<StoreReaderHold> PauseAllAsync(IReadOnlyList<PausedReader> readers, StoreReaderLease lease)
    {
        var pauser = Guid.NewGuid();
        var pausing = new List<(PausedReader Reader, Task Pause)>(readers.Count);
        var refused = new List<string>();
        var failures = new List<Exception>();
        foreach (var reader in readers)
        {
            try
            {
                pausing.Add((reader, reader.Pause(pauser, lease.Duration)));
            }
            catch (Exception ex)
            {
                refused.Add(reader.Name);
                failures.Add(ex);
            }
        }

        // Every reader the call reached may hold the pauser, including one whose answer will be lost.
        var hold = StoreReaderHold.Start(pauser, [.. pausing.Select(p => p.Reader)], lease);
        foreach (var (reader, pause) in pausing)
        {
            try
            {
                await pause;
            }
            catch (Exception ex)
            {
                refused.Add(reader.Name);
                failures.Add(ex);
            }
        }

        if (failures.Count == 0)
        {
            return hold;
        }

        await hold.DisposeAsync();
        throw new InvalidOperationException(
            $"The store readers of {string.Join(", ", refused)} could not be paused, so nothing was changed and the readers that had paused were resumed.",
            failures[0]);
    }

    /// <summary>Resumes every reader for <paramref name="pauser"/> and returns what failed, for a caller that is already carrying an exception.</summary>
    public static async Task<IReadOnlyList<(PausedReader Reader, Exception Failure)>> ResumeQuietlyAsync(IReadOnlyList<PausedReader> readers, Guid pauser)
    {
        var resuming = new List<(PausedReader Reader, Task Resume)>(readers.Count);
        var failures = new List<(PausedReader Reader, Exception Failure)>();
        foreach (var reader in readers)
        {
            try
            {
                resuming.Add((reader, reader.Resume(pauser)));
            }
            catch (Exception ex)
            {
                failures.Add((reader, ex));
            }
        }

        foreach (var (reader, resume) in resuming)
        {
            try
            {
                await resume;
            }
            catch (Exception ex)
            {
                failures.Add((reader, ex));
            }
        }

        return failures;
    }
}

/// <summary>
/// One pauser's hold over the store readers it paused. The hold renews every reader's pause at a third of the lease
/// until it is resumed or disposed; the renewal is one loop whose task the hold keeps and waits for before it resumes,
/// so no renewal is sent after the resume. A renewal that fails is tried again at the next one; one that keeps failing
/// lets the reader's pause lapse, which the reader logs. Disposing a hold that was not resumed resumes every reader and
/// reports nothing, for a caller that is already carrying an exception.
/// </summary>
internal sealed class StoreReaderHold : IAsyncDisposable
{
    private readonly Guid _pauser;
    private readonly IReadOnlyList<PausedReader> _readers;
    private readonly StoreReaderLease _lease;
    private PeriodicTimer? _renewal;
    private Task _renewing = Task.CompletedTask;
    private bool _released;

    private StoreReaderHold(Guid pauser, IReadOnlyList<PausedReader> readers, StoreReaderLease lease)
    {
        _pauser = pauser;
        _readers = readers;
        _lease = lease;
    }

    /// <summary>The pauser the readers hold.</summary>
    public Guid Pauser => _pauser;

    /// <summary>A hold over no reader, for a port whose readers cannot be paused.</summary>
    public static StoreReaderHold Nothing() => new(Guid.Empty, [], new StoreReaderLease(StoreReaderLease.DefaultDuration, TimeProvider.System));

    /// <summary>Holds the readers <paramref name="pauser"/> paused and starts renewing their pause.</summary>
    public static StoreReaderHold Start(Guid pauser, IReadOnlyList<PausedReader> readers, StoreReaderLease lease)
    {
        var hold = new StoreReaderHold(pauser, readers, lease);
        if (readers.Count > 0)
        {
            hold._renewal = new PeriodicTimer(lease.Renewal, lease.Clock);
            hold._renewing = hold.RenewAsync(hold._renewal);
        }

        return hold;
    }

    /// <summary>
    /// Pauses every reader again under the hold's pauser and waits until none has a batch in flight. A rebuild calls it
    /// after emptying the read model and before returning the checkpoints to the beginning a second time: a reader that
    /// read while it should have been paused — its pause lapsed, or its activation moved — may be half-way through a
    /// batch whose earlier entries the truncation removed, and its checkpoint must be written before the reset, not
    /// after it. A reader that still holds the pause only waits for nothing.
    /// </summary>
    /// <returns>A task that completes when every reader has answered.</returns>
    /// <exception cref="InvalidOperationException">A reader could not be paused again; the message names which.</exception>
    public async Task QuiesceAsync()
    {
        var pausing = new List<(PausedReader Reader, Task Pause)>(_readers.Count);
        var failures = new List<(PausedReader Reader, Exception Failure)>();
        foreach (var reader in _readers)
        {
            try
            {
                pausing.Add((reader, reader.Pause(_pauser, _lease.Duration)));
            }
            catch (Exception ex)
            {
                failures.Add((reader, ex));
            }
        }

        foreach (var (reader, pause) in pausing)
        {
            try
            {
                await pause;
            }
            catch (Exception ex)
            {
                failures.Add((reader, ex));
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"The store readers of {string.Join(", ", failures.Select(failure => failure.Reader.Name))} could not be paused again after the read model was emptied, so a batch they had in flight may not be read again.",
                failures[0].Failure);
        }
    }

    /// <summary>Stops renewing and resumes every reader, retrying one that fails once, and reports what stayed paused.</summary>
    /// <returns>A task that completes when every reader has been resumed.</returns>
    /// <exception cref="InvalidOperationException">
    /// A reader stayed paused and reads nothing until its pause lapses, within the lease; the message names which.
    /// </exception>
    public async Task ResumeAsync()
    {
        if (_released)
        {
            return;
        }

        await StopRenewingAsync();
        _released = true;
        var failures = await StoreReaderPause.ResumeQuietlyAsync(_readers, _pauser);
        if (failures.Count == 0)
        {
            return;
        }

        var retry = await StoreReaderPause.ResumeQuietlyAsync([.. failures.Select(failure => failure.Reader)], _pauser);
        if (retry.Count > 0)
        {
            throw new InvalidOperationException(
                $"The work finished, but the store readers of {string.Join(", ", retry.Select(failure => failure.Reader.Name))} stayed paused: " +
                $"each reads nothing until its pause lapses, within {_lease.Duration}.",
                retry[0].Failure);
        }
    }

    /// <summary>Stops renewing and, where the hold was not resumed, resumes every reader without reporting what failed.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_released)
        {
            await StopRenewingAsync();
            _released = true;
            await StoreReaderPause.ResumeQuietlyAsync(_readers, _pauser);
        }
    }

    /// <summary>Disposing the timer ends a wait for its next tick, and a renewal in flight is waited for.</summary>
    private async Task StopRenewingAsync()
    {
        _renewal?.Dispose();
        await _renewing;
    }

    private async Task RenewAsync(PeriodicTimer renewal)
    {
        while (await renewal.WaitForNextTickAsync())
        {
            await Task.WhenAll(_readers.Select(RenewQuietlyAsync));
        }
    }

    private async Task RenewQuietlyAsync(PausedReader reader)
    {
        try
        {
            await reader.Renew(_pauser, _lease.Duration);
        }
        catch (Exception ex)
        {
            _ = ex;
        }
    }
}

/// <summary>How long a pause lasts without a renewal, and the clock the pauser renews it on — at a third of the lease.</summary>
/// <param name="Duration">How long a reader stays paused after the last pause or renewal it received.</param>
/// <param name="Clock">The clock the renewals are timed on.</param>
internal sealed record StoreReaderLease(TimeSpan Duration, TimeProvider Clock)
{
    /// <summary>The lease a deployment runs with.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(60);

    /// <summary>How often a hold renews its pause: a third of the lease, so two renewals may be lost before it lapses.</summary>
    public TimeSpan Renewal => Duration / 3;
}

/// <summary>One store reader a rebuild, a replay or a reset pauses, and the name its failure is reported under.</summary>
/// <param name="Name">The name a failure is reported under — the reader's key.</param>
/// <param name="Pause">Pauses the reader for a pauser and a lease.</param>
/// <param name="Renew">Extends the pauser's pause by a lease from now.</param>
/// <param name="Resume">Releases the pauser's pause.</param>
internal readonly record struct PausedReader(string Name, Func<Guid, TimeSpan, Task> Pause, Func<Guid, TimeSpan, Task> Renew, Func<Guid, Task> Resume);
