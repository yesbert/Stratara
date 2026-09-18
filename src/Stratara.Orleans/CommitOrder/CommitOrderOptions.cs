using Stratara.Abstractions.CommitOrder;

namespace Stratara.Orleans.CommitOrder;

/// <summary>
/// Settings shared by every <see cref="ICommittedPositionReader"/>.
/// </summary>
public sealed class CommitOrderOptions
{
    /// <summary>
    /// The number of partitions the store's buckets are folded into. A partition is the unit a
    /// reader reads and a projection grain owns; the portable counter keeps one position per
    /// partition, so this is also the number of independent append serialisation points.
    /// </summary>
    /// <remarks>
    /// Under the portable counter the count is fixed once the store holds positions: lowering it merges partitions
    /// whose positions overlap, and the framework offers no renumbering. A host reading with the portable counter
    /// refuses to start with a count lower than the store's counter rows.
    /// </remarks>
    public int PartitionCount { get; set; } = 16;

    /// <summary>
    /// Whether the write context maintains the partition counter on every append. Off, the store
    /// pays nothing for the portable reader and that reader sees nothing; on, every append takes the
    /// partition's counter lock until it commits.
    /// </summary>
    /// <remarks>
    /// The framework does not read this value, and setting it changes nothing. Every process that appends to a store
    /// read by the portable counter adds <c>PartitionCounterInterceptor</c> to its write context's interceptors; an
    /// entry appended without it stops its partition's reader. The member is removed with the next major version.
    /// </remarks>
    [Obsolete("The framework does not read this value. A write context that maintains the partition counter adds PartitionCounterInterceptor to its interceptors; see the migration guide.")]
#pragma warning disable S1133 // Kept for source compatibility until the next major version, as the remarks state.
    public bool MaintainPartitionCounter { get; set; } = true;
#pragma warning restore S1133
}
