using System.Diagnostics.CodeAnalysis;

namespace Stratara.Shared.Partitioning;

/// <summary>
/// Framework-wide partitioning constants consumed by <see cref="BucketCalculator"/> and downstream
/// projection / saga workers.
/// </summary>
[ExcludeFromCodeCoverage]
public static class BucketConstants
{
    /// <summary>
    /// Total number of partitioning buckets. Chosen as a power of two large enough to keep
    /// per-bucket load reasonable while remaining cheap to model in the read-store schema.
    /// </summary>
    public const int TotalBucketCount = 4096;
}
