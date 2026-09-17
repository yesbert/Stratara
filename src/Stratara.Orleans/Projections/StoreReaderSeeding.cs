using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The seeding behind <see cref="IStoreReaderSeeding"/>: the consumer names come from the store readers registered in
/// the composition, the head of each partition from the registered reader, and a checkpoint is written under that
/// reader's name where the store reports none. The store reports <c>0</c> for a missing checkpoint and for one at the
/// beginning alike; both are seeded, because a consumer at the beginning has applied nothing it could lose.
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

                await checkpoints.SetAsync(consumer, partition, reader.Name, heads[partition], cancellationToken);
                seeded++;
            }
        }

        return new StoreReaderSeedingReport(seeded, existing);
    }
}
