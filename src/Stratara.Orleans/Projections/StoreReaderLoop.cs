using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Diagnostics;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The catch-up every store-reading grain runs: read after the checkpoint, apply batch by batch, and
/// move the checkpoint only past what applied. Shared by the projection grains and the saga grain,
/// which differ only in what "apply" means. The reader and the checkpoint store are scoped services
/// — they hold database context factories — so each catch-up resolves them in a scope of its own; a
/// grain lives in no scope.
/// </summary>
/// <remarks>
/// The loop keeps the position it last wrote and reads the checkpoint store only while it has none —
/// on activation, after <see cref="Invalidate"/>, which a grain calls when something may have
/// changed the checkpoint behind its back (a pause before a rebuild resets it), and after a catch-up that
/// failed — a checkpoint the store refused to advance among the reasons. The grain is the only writer of its
/// checkpoint otherwise, so the cached position is the stored one. A partition that
/// stops at an entry is logged with the entry and counted as stalled until it advances again, and the
/// time recorded with the oldest entry it has not applied is reported for the lag gauge. The token a
/// catch-up receives reaches every store call it makes, and a cancelled catch-up stops at the next
/// batch boundary without reading or writing again.
/// </remarks>
internal sealed class StoreReaderLoop(
    IServiceScopeFactory scopeFactory,
    ResiliencePipeline precedingFactPipeline,
    string consumer,
    int partition,
    int batchSize,
    ILogger logger)
{
    private readonly KeyValuePair<string, object?>[] _tags =
    [
        new(ApplicationDiagnostics.MetricTags.Projection, consumer),
        new(ApplicationDiagnostics.MetricTags.Partition, partition),
    ];

    private long _position;
    private bool _positionKnown;
    private long _invalidations;
    private bool _dirty;
    private bool _stalledOnEntry;
    private bool _stalledOnRead;
    private Task<int>? _running;

    /// <summary>The catch-up in flight, or <see langword="null"/> when none is.</summary>
    public Task<int>? Running => _running is { IsCompleted: false } running ? running : null;

    /// <summary>
    /// Forgets the cached position, so the next catch-up reads the checkpoint store first — also where a read of the
    /// checkpoint store was in flight when this was called, since it may have read the position this forgets.
    /// </summary>
    public void Invalidate()
    {
        _positionKnown = false;
        _invalidations++;
    }

    /// <summary>
    /// Withdraws what the loop reported: its stall and its lag. Called when the grain deactivates, so a
    /// reader that moved to another silo is not counted twice.
    /// </summary>
    public void Withdraw()
    {
        MarkAdvancing(onRead: true);
        MarkAdvancing(onRead: false);
        StoreReaderLag.Clear(consumer, partition);
    }

    /// <summary>
    /// Waits for the running loop, if any, to end — however it ends. A loop that failed has left
    /// its checkpoint where the failure was and will be reported to whoever requests the next one;
    /// a pause or a deactivation only needs it to be over.
    /// </summary>
    public async Task WaitForRunningAsync(CancellationToken cancellationToken)
    {
        if (Running is not { } running)
        {
            return;
        }

        try
        {
            await running.WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _ = ex;
        }
    }

    /// <summary>
    /// Marks the store as worth reading and returns the loop that will read it — the running one,
    /// which reads once more before it ends, or a new one. One loop runs at a time, which is the
    /// serialisation the grain's turn no longer provides once a nudge may interleave; every caller
    /// runs on the activation's scheduler, so the flag and the field need no lock. A loop that
    /// finds reading suspended forgets its position, because whoever suspends it — a pause, a
    /// replay — may change the checkpoint before reading resumes.
    /// </summary>
    /// <param name="applyBatch">What one batch means; see <see cref="CatchUpAsync(Func{CommittedBatch, CancellationToken, Task{int}}, Func{bool}, CancellationToken)"/>.</param>
    /// <param name="suspended">Whether reading is off for now — paused, or a replay is active.</param>
    /// <param name="cancellationToken">Stops a new loop at the next batch boundary; a running loop keeps the token it started with.</param>
    /// <returns>The loop; completes with how many entries it applied.</returns>
    public Task<int> RequestCatchUp(Func<CommittedBatch, CancellationToken, Task<int>> applyBatch, Func<bool> suspended, CancellationToken cancellationToken = default)
    {
        _dirty = true;
        if (Running is { } running)
        {
            return running;
        }

        _running = RunLoopAsync(applyBatch, suspended, cancellationToken);
        return _running;
    }

    private async Task<int> RunLoopAsync(Func<CommittedBatch, CancellationToken, Task<int>> applyBatch, Func<bool> suspended, CancellationToken cancellationToken)
    {
        var total = 0;
        while (_dirty && !cancellationToken.IsCancellationRequested)
        {
            _dirty = false;
            if (suspended())
            {
                Invalidate();
                break;
            }

            total += await CatchUpAsync(applyBatch, suspended, cancellationToken);
        }

        return total;
    }

    public async Task<long> PositionAsync()
    {
        if (_positionKnown)
        {
            return _position;
        }

        using var scope = scopeFactory.CreateScope();
        var (reader, checkpoints) = Resolve(scope.ServiceProvider);
        return await checkpoints.GetAsync(consumer, partition, reader.Name);
    }

    /// <summary>
    /// Reads and applies until the store has nothing newer, or until a batch applies only partly.
    /// </summary>
    /// <param name="applyBatch">
    /// Applies a batch in order and returns the index of the first entry it did not apply — the
    /// batch's count when every entry applied. <see cref="ApplyEachAsync"/> is the usual body.
    /// </param>
    /// <returns>How many entries applied.</returns>
    public Task<int> CatchUpAsync(Func<CommittedBatch, CancellationToken, Task<int>> applyBatch) =>
        CatchUpAsync(applyBatch, static () => false, CancellationToken.None);

    /// <summary>
    /// Reads and applies until a batch says the store had nothing more, a batch applies only partly,
    /// <paramref name="suspended"/> says to stop at the next batch boundary — a pause does not wait for a
    /// partition that is far behind to catch up first — or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<int> CatchUpAsync(Func<CommittedBatch, CancellationToken, Task<int>> applyBatch, Func<bool> suspended, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAndApplyAsync(applyBatch, suspended, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Invalidate();
            MarkStalled(onRead: true);
            logger.LogCatchUpFaulted(ex, consumer, partition);
            throw;
        }
    }

    /// <summary>
    /// The catch-up proper. A store that cannot be read, a checkpoint the store refuses or a checkpoint that cannot
    /// be written throws out of here and is counted and logged by the caller as a stall on the read, and the caller
    /// forgets the cached position, so the next catch-up starts from what the checkpoint store holds. The checkpoint
    /// is advanced from the position the loop last saw, so a store that guards it refuses a write from an activation
    /// that another has overtaken; an entry that
    /// cannot be applied is the batch's own affair. The checkpoint for the entries a cut batch applied is written
    /// with a token of its own, because those entries are applied whether or not the catch-up was cancelled.
    /// </summary>
    private async Task<int> ReadAndApplyAsync(Func<CommittedBatch, CancellationToken, Task<int>> applyBatch, Func<bool> suspended, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var (reader, checkpoints) = Resolve(scope.ServiceProvider);
        var readerName = reader.Name;

        if (!_positionKnown)
        {
            var invalidations = _invalidations;
            _position = await checkpoints.GetAsync(consumer, partition, readerName, cancellationToken);
            _positionKnown = invalidations == _invalidations;
        }

        var total = 0;

        while (!suspended() && !cancellationToken.IsCancellationRequested)
        {
            var batch = await reader.ReadAfterAsync(partition, _position, batchSize, cancellationToken);
            MarkAdvancing(onRead: true);
            if (batch.Entries.Count == 0)
            {
                StoreReaderLag.Clear(consumer, partition);
                return total;
            }

            StoreReaderLag.Report(consumer, partition, batch.Entries[0].Entry.Timestamp);
            var applied = await applyBatch(batch, cancellationToken);
            total += applied;
            if (applied > 0)
            {
                ApplicationDiagnostics.Metrics.OrleansReaderApplied.Add(applied, _tags);
            }

            if (applied < batch.Entries.Count)
            {
                StoreReaderLag.Report(consumer, partition, batch.Entries[applied].Entry.Timestamp);
                var resumeAt = batch.ResumePositionBefore(applied, _position);
                if (resumeAt != _position)
                {
                    await checkpoints.AdvanceAsync(consumer, partition, readerName, _position, resumeAt, CancellationToken.None);
                    _position = resumeAt;
                }

                return total;
            }

            MarkAdvancing(onRead: false);
            await checkpoints.AdvanceAsync(consumer, partition, readerName, _position, batch.Position, cancellationToken);
            _position = batch.Position;
            if (!batch.HasMore)
            {
                StoreReaderLag.Clear(consumer, partition);
                return total;
            }
        }

        return total;
    }

    /// <summary>
    /// Applies a batch entry by entry under the preceding-fact retry policy and stops at the first
    /// entry that fails, so the caller's checkpoint never passes an entry that did not apply. Every
    /// failed attempt is logged; the entry the batch stops at is logged and counts the partition as
    /// stalled. A cancellation is not a failure: it ends the batch without counting a stall.
    /// </summary>
    /// <returns>The index of the first entry that did not apply, or the batch's count.</returns>
    public async Task<int> ApplyEachAsync(CommittedBatch batch, Func<EventStreamEntry, CancellationToken, Task> applyEntry, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < batch.Entries.Count; i++)
        {
            var entry = batch.Entries[i].Entry;
            var attempt = 0;
            try
            {
                await precedingFactPipeline.ExecuteAsync(async ct =>
                {
                    attempt++;
                    try
                    {
                        await applyEntry(entry, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogEntryAttemptFailed(ex, consumer, partition, entry.Id, attempt);
                        throw;
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return i;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MarkStalled(onRead: false);
                logger.LogPartitionStalled(ex, consumer, partition, entry.Id, entry.SequenceNumber);
                return i;
            }
        }

        MarkAdvancing(onRead: false);
        return batch.Entries.Count;
    }

    /// <summary>Whether the partition counts as stalled: on an entry it cannot apply, or on a read it cannot make.</summary>
    private bool Stalled => _stalledOnEntry || _stalledOnRead;

    private void MarkStalled(bool onRead)
    {
        var before = Stalled;
        if (onRead)
        {
            _stalledOnRead = true;
        }
        else
        {
            _stalledOnEntry = true;
        }

        if (!before)
        {
            ApplicationDiagnostics.Metrics.OrleansReaderStalled.Add(1, _tags);
        }
    }

    private void MarkAdvancing(bool onRead)
    {
        var before = Stalled;
        if (onRead)
        {
            _stalledOnRead = false;
        }
        else
        {
            _stalledOnEntry = false;
        }

        if (before && !Stalled)
        {
            ApplicationDiagnostics.Metrics.OrleansReaderStalled.Add(-1, _tags);
        }
    }

    private static (ICommittedPositionReader Reader, IProjectionCheckpointStore Checkpoints) Resolve(IServiceProvider services) =>
        (services.GetRequiredService<ICommittedPositionReader>(), services.GetRequiredService<IProjectionCheckpointStore>());
}
