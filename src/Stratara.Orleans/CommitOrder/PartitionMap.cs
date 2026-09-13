namespace Stratara.Orleans.CommitOrder;

/// <summary>
/// Folds the store's bucket ids into the smaller set of partitions the readers and grains work in.
/// </summary>
public static class PartitionMap
{
    /// <summary>Returns the partition <paramref name="bucketId"/> belongs to.</summary>
    /// <param name="bucketId">A bucket id as the store assigns it.</param>
    /// <param name="partitionCount">The configured number of partitions.</param>
    /// <returns>A partition index from <c>0</c> to <paramref name="partitionCount"/> − 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="partitionCount"/> is not positive.</exception>
    public static int PartitionOf(int bucketId, int partitionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        return Math.Abs(bucketId % partitionCount);
    }
}
