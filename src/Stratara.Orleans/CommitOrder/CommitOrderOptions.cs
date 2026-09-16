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
    /// The framework does not read this value: a write context reads it when it decides whether to add
    /// <c>PartitionCounterInterceptor</c> to its interceptors. Every process that appends to a store read by the
    /// portable counter must add the interceptor; an entry appended without it stops its partition's reader.
    /// </remarks>
    public bool MaintainPartitionCounter { get; set; } = true;
}
