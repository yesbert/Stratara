using Microsoft.Extensions.DependencyInjection;
using Polly;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The catch-up every store-reading grain runs: read after the checkpoint, apply entry by entry
/// under the retry policy for a missing prerequisite, and move the checkpoint only past what
/// applied. Shared by the projection grains and the saga grain, which differ only in what "apply"
/// means. The reader and the checkpoint store are scoped services — they hold database context
/// factories — so each catch-up resolves them in a scope of its own; a grain lives in no scope.
/// </summary>
internal sealed class StoreReaderLoop(
    IServiceScopeFactory scopeFactory,
    ResiliencePipeline precedingFactPipeline,
    string consumer,
    int partition,
    int batchSize)
{
    public async Task<long> PositionAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var (reader, checkpoints) = Resolve(scope.ServiceProvider);
        return await checkpoints.GetAsync(consumer, partition, reader.GetType().Name);
    }

    public async Task<int> CatchUpAsync(Func<EventStreamEntry, CancellationToken, Task> applyEntry)
    {
        using var scope = scopeFactory.CreateScope();
        var (reader, checkpoints) = Resolve(scope.ServiceProvider);
        var readerName = reader.GetType().Name;

        var position = await checkpoints.GetAsync(consumer, partition, readerName);
        var total = 0;

        while (true)
        {
            var batch = await reader.ReadAfterAsync(partition, position, batchSize);
            if (batch.Entries.Count == 0)
            {
                return total;
            }

            var applied = await ApplyAsync(batch, applyEntry);
            total += applied;

            if (applied < batch.Entries.Count)
            {
                var resumeAt = batch.ResumePositionBefore(applied, position);
                if (resumeAt != position)
                {
                    await checkpoints.SetAsync(consumer, partition, readerName, resumeAt);
                }

                return total;
            }

            position = batch.Position;
            await checkpoints.SetAsync(consumer, partition, readerName, position);
        }
    }

    private static (ICommittedPositionReader Reader, IProjectionCheckpointStore Checkpoints) Resolve(IServiceProvider services) =>
        (services.GetRequiredService<ICommittedPositionReader>(), services.GetRequiredService<IProjectionCheckpointStore>());

    private async Task<int> ApplyAsync(CommittedBatch batch, Func<EventStreamEntry, CancellationToken, Task> applyEntry)
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
}
