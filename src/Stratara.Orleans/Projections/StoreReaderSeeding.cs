using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Sagas;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The seeding behind <see cref="IStoreReaderSeeding"/>: the consumer names come from the store readers registered in
/// the composition, the head of each partition from the registered reader, and a checkpoint is written under that
/// reader's name where the store reports none. The store reports <c>0</c> for a missing checkpoint and for one at the
/// beginning alike; both are seeded, because a consumer at the beginning has applied nothing it could lose. A saga of a
/// store whose sagas shared one checkpoint before 4.2.0 is seeded where its reader would start it — at the shared
/// checkpoint, or further where another saga of the host is — so that the shared reader's backlog is not skipped.
/// </summary>
internal sealed class StoreReaderSeeding(
    IEnumerable<INudgeTarget> storeReaders,
    ICommittedPositionReader reader,
    IProjectionCheckpointStore checkpoints,
    IOptions<CommitOrderOptions> commitOrder) : IStoreReaderSeeding
{
    public async Task<StoreReaderSeedingReport> SeedAtHeadAsync(CancellationToken cancellationToken = default)
    {
        var consumers = storeReaders.SelectMany(target => target.ConsumerNames).Distinct(StringComparer.Ordinal).ToList();
        var partitions = commitOrder.Value.PartitionCount;
        var heads = new long[partitions];
        for (var partition = 0; partition < partitions; partition++)
        {
            heads[partition] = await reader.HeadAsync(partition, cancellationToken);
        }

        var sagas = storeReaders.OfType<SagaNudgeTarget>().SelectMany(target => target.ConsumerNames).ToHashSet(StringComparer.Ordinal);
        var sagaStarts = new long?[partitions];
        for (var partition = 0; partition < partitions && sagas.Count > 0; partition++)
        {
            sagaStarts[partition] = await SharedSagaStartAsync(sagas, partition, cancellationToken);
        }

        var seeded = 0;
        var existing = 0;
        foreach (var consumer in consumers)
        {
            for (var partition = 0; partition < partitions; partition++)
            {
                if (await checkpoints.GetAsync(consumer, partition, reader.Name, cancellationToken) != 0)
                {
                    existing++;
                    continue;
                }

                var position = sagas.Contains(consumer) && sagaStarts[partition] is { } shared ? shared : heads[partition];
                await checkpoints.SetAsync(consumer, partition, reader.Name, position, cancellationToken);
                seeded++;
            }
        }

        return new StoreReaderSeedingReport(seeded, existing);
    }

    /// <summary>
    /// Where a saga of a store its sagas read with one shared checkpoint before 4.2.0 is seeded: where the saga reader
    /// would start it, not at the head, because the shared reader may not have applied everything below the head. A
    /// partition without the shared checkpoint answers <see langword="null"/> and is seeded at the head.
    /// </summary>
    private async Task<long?> SharedSagaStartAsync(HashSet<string> sagas, int partition, CancellationToken cancellationToken)
    {
        var shared = await checkpoints.GetAsync(SagaGrain.ConsumerName, partition, reader.Name, cancellationToken);
        if (shared == 0)
        {
            return null;
        }

        var held = new List<long?>(sagas.Count);
        foreach (var saga in sagas)
        {
            held.Add(await checkpoints.GetAsync(saga, partition, reader.Name, cancellationToken));
        }

        return SagaStart.StartingPosition(shared, held);
    }
}
