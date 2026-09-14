using Microsoft.Extensions.DependencyInjection;
using Polly;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;

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
/// on activation, and after <see cref="Invalidate"/>, which a grain calls when something may have
/// changed the checkpoint behind its back (a pause before a rebuild resets it). The grain is the only
/// writer of its checkpoint otherwise, so the cached position is the stored one.
/// </remarks>
internal sealed class StoreReaderLoop(
    IServiceScopeFactory scopeFactory,
    ResiliencePipeline precedingFactPipeline,
    string consumer,
    int partition,
    int batchSize)
{
    private long _position;
    private bool _positionKnown;
    private bool _dirty;
    private Task<int>? _running;

    /// <summary>The catch-up in flight, or <see langword="null"/> when none is.</summary>
    public Task<int>? Running => _running is { IsCompleted: false } running ? running : null;

    /// <summary>Forgets the cached position, so the next catch-up reads the checkpoint store first.</summary>
    public void Invalidate() => _positionKnown = false;

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
    /// <param name="applyBatch">What one batch means; see <see cref="CatchUpAsync(Func{CommittedBatch, Task{int}}, Func{bool})"/>.</param>
    /// <param name="suspended">Whether reading is off for now — paused, or a replay is active.</param>
    /// <returns>The loop; completes with how many entries it applied.</returns>
    public Task<int> RequestCatchUp(Func<CommittedBatch, Task<int>> applyBatch, Func<bool> suspended)
    {
        _dirty = true;
        if (Running is { } running)
        {
            return running;
        }

        _running = RunLoopAsync(applyBatch, suspended);
        return _running;
    }

    private async Task<int> RunLoopAsync(Func<CommittedBatch, Task<int>> applyBatch, Func<bool> suspended)
    {
        var total = 0;
        while (_dirty)
        {
            _dirty = false;
            if (suspended())
            {
                Invalidate();
                break;
            }

            total += await CatchUpAsync(applyBatch, suspended);
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
        return await checkpoints.GetAsync(consumer, partition, reader.GetType().Name);
    }

    /// <summary>
    /// Reads and applies until the store has nothing newer, or until a batch applies only partly.
    /// </summary>
    /// <param name="applyBatch">
    /// Applies a batch in order and returns the index of the first entry it did not apply — the
    /// batch's count when every entry applied. <see cref="ApplyEachAsync"/> is the usual body.
    /// </param>
    /// <returns>How many entries applied.</returns>
    public Task<int> CatchUpAsync(Func<CommittedBatch, Task<int>> applyBatch) => CatchUpAsync(applyBatch, static () => false);

    /// <summary>
    /// Reads and applies until the store has nothing newer, a batch applies only partly, or
    /// <paramref name="suspended"/> says to stop at the next batch boundary — a pause does not wait
    /// for a partition that is far behind to catch up first.
    /// </summary>
    public async Task<int> CatchUpAsync(Func<CommittedBatch, Task<int>> applyBatch, Func<bool> suspended)
    {
        using var scope = scopeFactory.CreateScope();
        var (reader, checkpoints) = Resolve(scope.ServiceProvider);
        var readerName = reader.GetType().Name;

        if (!_positionKnown)
        {
            _position = await checkpoints.GetAsync(consumer, partition, readerName);
            _positionKnown = true;
        }

        var total = 0;

        while (!suspended())
        {
            var batch = await reader.ReadAfterAsync(partition, _position, batchSize);
            if (batch.Entries.Count == 0)
            {
                return total;
            }

            var applied = await applyBatch(batch);
            total += applied;

            if (applied < batch.Entries.Count)
            {
                var resumeAt = batch.ResumePositionBefore(applied, _position);
                if (resumeAt != _position)
                {
                    await checkpoints.SetAsync(consumer, partition, readerName, resumeAt);
                    _position = resumeAt;
                }

                return total;
            }

            await checkpoints.SetAsync(consumer, partition, readerName, batch.Position);
            _position = batch.Position;
        }

        return total;
    }

    /// <summary>
    /// Applies a batch entry by entry under the preceding-fact retry policy and stops at the first
    /// entry that fails, so the caller's checkpoint never passes an entry that did not apply.
    /// </summary>
    /// <returns>The index of the first entry that did not apply, or the batch's count.</returns>
    public async Task<int> ApplyEachAsync(CommittedBatch batch, Func<EventStreamEntry, CancellationToken, Task> applyEntry)
    {
        for (var i = 0; i < batch.Entries.Count; i++)
        {
            var entry = batch.Entries[i].Entry;
            try
            {
                await precedingFactPipeline.ExecuteAsync(async ct => await applyEntry(entry, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return i;
            }
        }

        return batch.Entries.Count;
    }

    private static (ICommittedPositionReader Reader, IProjectionCheckpointStore Checkpoints) Resolve(IServiceProvider services) =>
        (services.GetRequiredService<ICommittedPositionReader>(), services.GetRequiredService<IProjectionCheckpointStore>());
}
