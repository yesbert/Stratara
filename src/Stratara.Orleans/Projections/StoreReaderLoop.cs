using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Diagnostics;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Projections;

/// <summary>What applying one batch came to: how far it got, and how many of those entries handed anything to a handler.</summary>
/// <param name="Processed">The index of the first entry not applied — the batch's count when every entry applied.</param>
/// <param name="Effective">How many of the applied entries handed anything to a handler.</param>
internal readonly record struct BatchOutcome(int Processed, int Effective)
{
    /// <summary>Every one of <paramref name="processed"/> entries counts as effective: what a batch that cannot tell reports.</summary>
    public static BatchOutcome AllEffective(int processed) => new(processed, processed);
}

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
/// failed — a checkpoint the store refused to advance among the reasons. Before it first reads the checkpoint it
/// runs the grain's start once, which may give a consumer without a checkpoint one; a start that fails is run again
/// by the next catch-up. The grain is the only writer of its
/// checkpoint otherwise, so the cached position is the stored one — or, where a batch applied nothing and
/// <see cref="IdleCheckpointInterval"/> lets the write wait, ahead of it: the position past such a batch is written with
/// the next batch that applies something, by the first catch-up that ends once the interval has passed, or by
/// <see cref="FlushDeferredAsync"/>, and forgetting the position forgets that write, since whoever made the loop forget
/// may have changed the checkpoint. A partition that
/// stops at an entry is logged with the entry and counted as stalled until it advances again, and the
/// time recorded with the oldest entry it has not applied is reported for the lag gauge. The token a
/// catch-up receives reaches every store read it makes, and a cancelled catch-up stops at the next
/// batch boundary without reading again; the checkpoint for the entries it applied is written whatever the token says.
/// </remarks>
internal sealed class StoreReaderLoop(
    IServiceScopeFactory scopeFactory,
    ResiliencePipeline precedingFactPipeline,
    string consumer,
    int partition,
    int batchSize,
    ILogger logger,
    Func<IProjectionCheckpointStore, string, CancellationToken, Task>? start = null)
{
    private readonly KeyValuePair<string, object?>[] _tags =
    [
        new(ApplicationDiagnostics.MetricTags.Projection, consumer),
        new(ApplicationDiagnostics.MetricTags.Partition, partition),
    ];

    private long _position;
    private long _written;
    private long? _deferredSince;
    private bool _positionKnown;
    private long _invalidations;
    private bool _started = start is null;
    private bool _dirty;
    private bool _stalledOnEntry;
    private bool _stalledOnRead;
    private Task<int>? _running;

    /// <summary>The catch-up in flight, or <see langword="null"/> when none is.</summary>
    public Task<int>? Running => _running is { IsCompleted: false } running ? running : null;

    /// <summary>
    /// Called whenever the loop has learnt or moved its position — read the checkpoint, or written it — on the scheduler
    /// the loop runs on, so a grain can answer whoever waits for a position.
    /// </summary>
    public Action? PositionChanged { get; set; }

    /// <summary>
    /// How long the position past batches that applied nothing may go unwritten; <see cref="TimeSpan.Zero"/>, the default,
    /// writes it after every batch.
    /// </summary>
    public TimeSpan IdleCheckpointInterval { get; init; }

    /// <summary>Whether the loop holds a position it has not written and the interval for writing it has passed.</summary>
    public bool DeferralDue => _positionKnown && _deferredSince is { } since && Stopwatch.GetElapsedTime(since) >= IdleCheckpointInterval;

    /// <summary>Whether the partition is stopped at an entry it cannot apply.</summary>
    public bool StalledOnEntry => _stalledOnEntry;

    /// <summary>Whether the partition's last read of the store or of its checkpoint failed.</summary>
    public bool StalledOnRead => _stalledOnRead;

    /// <summary>The position the loop last read or wrote, where it still knows it.</summary>
    /// <param name="position">The position, or <c>0</c> where the loop does not know it.</param>
    /// <returns>Whether the loop knows its position.</returns>
    public bool TryGetPosition(out long position)
    {
        position = _positionKnown ? _position : 0;
        return _positionKnown;
    }

    /// <summary>
    /// Forgets the cached position, so the next catch-up reads the checkpoint store first — also where a read of the
    /// checkpoint store was in flight when this was called, since it may have read the position this forgets.
    /// </summary>
    public void Invalidate()
    {
        _positionKnown = false;
        _deferredSince = null;
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
    /// <param name="applyBatch">What one batch means; see <see cref="CatchUpAsync(Func{CommittedBatch, CancellationToken, Task{BatchOutcome}}, Func{bool}, CancellationToken)"/>.</param>
    /// <param name="suspended">Whether reading is off for now — paused, or a replay is active.</param>
    /// <param name="cancellationToken">Stops a new loop at the next batch boundary; a running loop keeps the token it started with.</param>
    /// <returns>The loop; completes with how many entries it applied.</returns>
    public Task<int> RequestCatchUp(Func<CommittedBatch, CancellationToken, Task<BatchOutcome>> applyBatch, Func<bool> suspended, CancellationToken cancellationToken = default)
    {
        _dirty = true;
        if (Running is { } running)
        {
            return running;
        }

        _running = RunLoopAsync(applyBatch, suspended, cancellationToken);
        return _running;
    }

    private async Task<int> RunLoopAsync(Func<CommittedBatch, CancellationToken, Task<BatchOutcome>> applyBatch, Func<bool> suspended, CancellationToken cancellationToken)
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

        PositionChanged?.Invoke();
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
    /// <see cref="CatchUpAsync(Func{CommittedBatch, CancellationToken, Task{BatchOutcome}}, Func{bool}, CancellationToken)"/> for a batch that
    /// reports only how far it got: every entry it applied counts as effective, so the checkpoint is written after every batch.
    /// </summary>
    public Task<int> CatchUpAsync(Func<CommittedBatch, CancellationToken, Task<int>> applyBatch, Func<bool> suspended, CancellationToken cancellationToken) =>
        CatchUpAsync(async (batch, token) => BatchOutcome.AllEffective(await applyBatch(batch, token)), suspended, cancellationToken);

    /// <summary>
    /// Reads and applies until a batch says the store had nothing more, a batch applies only partly,
    /// <paramref name="suspended"/> says to stop at the next batch boundary — a pause does not wait for a
    /// partition that is far behind to catch up first — or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<int> CatchUpAsync(Func<CommittedBatch, CancellationToken, Task<BatchOutcome>> applyBatch, Func<bool> suspended, CancellationToken cancellationToken)
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
    /// cannot be applied is the batch's own affair. The checkpoint for the entries a batch applied — cut or whole — is
    /// written with a token of its own, because those entries are applied whether or not the catch-up was cancelled. A
    /// position the loop held back is written once the catch-up ends, where the interval has passed and reading is not
    /// suspended.
    /// </summary>
    private async Task<int> ReadAndApplyAsync(Func<CommittedBatch, CancellationToken, Task<BatchOutcome>> applyBatch, Func<bool> suspended, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var (reader, checkpoints) = Resolve(scope.ServiceProvider);
        var readerName = reader.Name;
        await EnsurePositionAsync(checkpoints, readerName, cancellationToken);

        var total = await ReadAndApplyBatchesAsync(reader, checkpoints, applyBatch, suspended, cancellationToken);
        if (DeferralDue && !suspended())
        {
            await WriteAsync(checkpoints, readerName, _position, CancellationToken.None);
        }

        return total;
    }

    /// <summary>
    /// Writes the position the loop holds back, if it holds one, so a deactivation does not leave a successor to read
    /// again what applied nothing. Called only while no catch-up runs and reading is not suspended; a write that fails or
    /// is cancelled is given up, and the successor reads those entries again.
    /// </summary>
    public async Task FlushDeferredAsync(CancellationToken cancellationToken)
    {
        if (!_positionKnown || _deferredSince is null || Running is not null)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var (reader, checkpoints) = Resolve(scope.ServiceProvider);
            await WriteAsync(checkpoints, reader.Name, _position, cancellationToken);
        }
        catch (Exception ex)
        {
            _ = ex;
            Invalidate();
        }
    }

    /// <summary>Advances the checkpoint from the position last written to <paramref name="to"/>, and holds nothing back afterwards.</summary>
    private async Task WriteAsync(IProjectionCheckpointStore checkpoints, string readerName, long to, CancellationToken cancellationToken)
    {
        await checkpoints.AdvanceAsync(consumer, partition, readerName, _written, to, cancellationToken);
        _written = to;
        _position = to;
        _deferredSince = null;
        PositionChanged?.Invoke();
    }

    private async Task<int> ReadAndApplyBatchesAsync(
        ICommittedPositionReader reader,
        IProjectionCheckpointStore checkpoints,
        Func<CommittedBatch, CancellationToken, Task<BatchOutcome>> applyBatch,
        Func<bool> suspended,
        CancellationToken cancellationToken)
    {
        var readerName = reader.Name;
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
            var outcome = await applyBatch(batch, cancellationToken);
            var applied = outcome.Processed;
            total += applied;
            if (applied > 0)
            {
                ApplicationDiagnostics.Metrics.OrleansReaderApplied.Add(applied, _tags);
            }

            if (applied < batch.Entries.Count)
            {
                await StopAtAsync(checkpoints, readerName, batch, applied);
                return total;
            }

            MarkAdvancing(onRead: false);
            await PassAsync(checkpoints, readerName, batch, outcome);
            if (!batch.HasMore)
            {
                StoreReaderLag.Clear(consumer, partition);
                return total;
            }
        }

        return total;
    }

    /// <summary>
    /// Writes the position before the entry a cut batch stopped at, so the stall is visible in the store — also where the
    /// loop held a position back before it.
    /// </summary>
    private async Task StopAtAsync(IProjectionCheckpointStore checkpoints, string readerName, CommittedBatch batch, int applied)
    {
        StoreReaderLag.Report(consumer, partition, batch.Entries[applied].Entry.Timestamp);
        var resumeAt = batch.ResumePositionBefore(applied, _position);
        if (resumeAt != _written)
        {
            await WriteAsync(checkpoints, readerName, resumeAt, CancellationToken.None);
        }
    }

    /// <summary>
    /// Moves past a whole batch: writes the checkpoint where the batch applied something or nothing may be held back, and
    /// otherwise holds the position back, starting the interval it may be held for.
    /// </summary>
    private async Task PassAsync(IProjectionCheckpointStore checkpoints, string readerName, CommittedBatch batch, BatchOutcome outcome)
    {
        if (outcome.Effective > 0 || IdleCheckpointInterval <= TimeSpan.Zero)
        {
            await WriteAsync(checkpoints, readerName, batch.Position, CancellationToken.None);
            return;
        }

        _position = batch.Position;
        _deferredSince ??= Stopwatch.GetTimestamp();
        PositionChanged?.Invoke();
    }

    /// <summary>
    /// Applies a batch entry by entry under the preceding-fact retry policy and stops at the first
    /// entry that fails, so the caller's checkpoint never passes an entry that did not apply. Every
    /// failed attempt is logged; the entry the batch stops at is logged and counts the partition as
    /// stalled. A cancellation is not a failure: it ends the batch without counting a stall.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="applyEntry">Applies one entry; answers whether it handed anything to a handler.</param>
    /// <param name="cancellationToken">Ends the batch at the entry it reaches.</param>
    /// <returns>The index of the first entry that did not apply, or the batch's count, and how many applied entries were effective.</returns>
    public async Task<BatchOutcome> ApplyEachAsync(CommittedBatch batch, Func<EventStreamEntry, CancellationToken, Task<bool>> applyEntry, CancellationToken cancellationToken = default)
    {
        var effective = 0;
        for (var i = 0; i < batch.Entries.Count; i++)
        {
            var entry = batch.Entries[i].Entry;
            var attempt = 0;
            try
            {
                var handled = false;
                await precedingFactPipeline.ExecuteAsync(async ct =>
                {
                    attempt++;
                    try
                    {
                        handled = await applyEntry(entry, ct);
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or CommittedEventsNotPublishedException))
                    {
                        logger.LogEntryAttemptFailed(ex, consumer, partition, entry.Id, attempt);
                        throw;
                    }
                }, cancellationToken);
                effective += handled ? 1 : 0;
            }
            catch (CommittedEventsNotPublishedException committed)
            {
                // The handler's events are recorded. Applying the entry again would record them a second time.
                logger.LogEntryCommittedNotPublished(committed, consumer, partition, entry.Id);
                effective++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new BatchOutcome(i, effective);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MarkStalled(onRead: false);
                logger.LogPartitionStalled(ex, consumer, partition, entry.Id, entry.SequenceNumber);
                return new BatchOutcome(i, effective);
            }
        }

        MarkAdvancing(onRead: false);
        return new BatchOutcome(batch.Entries.Count, effective);
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

    /// <summary>
    /// Reads the checkpoint where the cached position was forgotten, after the grain's start where it has not run yet. A
    /// position read while an invalidation arrived stays unknown, so the next catch-up reads the checkpoint again.
    /// </summary>
    private async Task EnsurePositionAsync(IProjectionCheckpointStore checkpoints, string readerName, CancellationToken cancellationToken)
    {
        if (_positionKnown)
        {
            return;
        }

        if (!_started && start is not null)
        {
            await start(checkpoints, readerName, cancellationToken);
            _started = true;
        }

        var invalidations = _invalidations;
        _position = await checkpoints.GetAsync(consumer, partition, readerName, cancellationToken);
        _written = _position;
        _deferredSince = null;
        _positionKnown = invalidations == _invalidations;
        PositionChanged?.Invoke();
    }

    private static (ICommittedPositionReader Reader, IProjectionCheckpointStore Checkpoints) Resolve(IServiceProvider services) =>
        (services.GetRequiredService<ICommittedPositionReader>(), services.GetRequiredService<IProjectionCheckpointStore>());
}
