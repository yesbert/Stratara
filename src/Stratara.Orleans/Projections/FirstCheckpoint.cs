using Stratara.Abstractions.Projections;

namespace Stratara.Orleans.Projections;

/// <summary>
/// What a checkpoint store can say beyond the port: whether a consumer has a checkpoint in a partition at all — the
/// port reports <c>0</c> for a missing one and for one at the beginning alike — and a first checkpoint written only where
/// none exists, so that two writers of it never overwrite one another. The framework's store implements it.
/// </summary>
internal interface IFirstCheckpointStore
{
    /// <summary>Whether the consumer has a checkpoint in the partition, under whichever reader.</summary>
    Task<bool> ExistsAsync(string consumer, int partition, CancellationToken cancellationToken);

    /// <summary>Writes the checkpoint where the consumer has none in the partition; returns whether it wrote it.</summary>
    Task<bool> CreateAsync(string consumer, int partition, string reader, long position, CancellationToken cancellationToken);
}

/// <summary>A consumer's first checkpoint, through the store's own answer where it gives one and through the port otherwise.</summary>
internal static class FirstCheckpoint
{
    /// <summary>
    /// The store's own answer, or the port's where the store gives none: a checkpoint at <c>0</c> counts as missing
    /// there, and a first checkpoint is written as an advance from <c>0</c>, which a store that guards its writes
    /// refuses where another writer came first.
    /// </summary>
    public static IFirstCheckpointStore Of(IProjectionCheckpointStore checkpoints, string reader) =>
        checkpoints as IFirstCheckpointStore ?? new ThroughThePort(checkpoints, reader);

    private sealed class ThroughThePort(IProjectionCheckpointStore checkpoints, string readerName) : IFirstCheckpointStore
    {
        public async Task<bool> ExistsAsync(string consumer, int partition, CancellationToken cancellationToken) =>
            await checkpoints.GetAsync(consumer, partition, readerName, cancellationToken) != 0;

        public async Task<bool> CreateAsync(string consumer, int partition, string reader, long position, CancellationToken cancellationToken)
        {
            if (position == 0 || await ExistsAsync(consumer, partition, cancellationToken))
            {
                return false;
            }

            try
            {
                await checkpoints.AdvanceAsync(consumer, partition, reader, 0, position, cancellationToken);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
