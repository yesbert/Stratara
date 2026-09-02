namespace Stratara.Abstractions.Entities;

/// <summary>
/// Persistence-level entity carrying a bucket id used by the command-bus serialisation
/// layer to dispatch aggregate-scoped commands single-writer per bucket.
/// </summary>
/// <remarks>
/// Computed via <c>BucketCalculator.GetBucketId(aggregateId)</c> — hash modulo
/// <see cref="Partitioning.BucketLockPool.BucketCount"/>. Collisions within a bucket are tolerated
/// (they just serialise more than strictly needed).
/// </remarks>
public interface IBucket
{
    /// <summary>The bucket index in <c>[0, <see cref="Partitioning.BucketLockPool.BucketCount"/>)</c>.</summary>
    int BucketId { get; set; }
}
