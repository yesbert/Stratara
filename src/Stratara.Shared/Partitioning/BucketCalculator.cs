namespace Stratara.Shared.Partitioning;

/// <summary>
/// Pure-function bucket id calculator. Maps a <see cref="Guid"/> identifier to a stable bucket
/// index in the closed range <c>[0, BucketConstants.TotalBucketCount)</c> by absolute-hash modulo.
/// Used by projection / saga partitioning to spread work deterministically across workers.
/// </summary>
public static class BucketCalculator
{
    /// <summary>
    /// Returns a stable bucket id in <c>[0, BucketConstants.TotalBucketCount)</c> for the given
    /// <paramref name="id"/>.
    /// </summary>
    /// <param name="id">The identifier to hash.</param>
    /// <returns>A non-negative bucket index smaller than <see cref="BucketConstants.TotalBucketCount"/>.</returns>
    public static int GetBucketId(Guid id)
    {
        // Mask the sign bit instead of Math.Abs — Math.Abs(int.MinValue) throws OverflowException,
        // and int.MinValue is a reachable Guid.GetHashCode() output with non-zero probability.
        var hash = id.GetHashCode() & int.MaxValue;
        return hash % BucketConstants.TotalBucketCount;
    }
}
