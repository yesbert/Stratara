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
    public int PartitionCount { get; set; } = 16;

    /// <summary>
    /// Whether the write context maintains the partition counter on every append. Off, the store
    /// pays nothing for the portable reader and that reader sees nothing; on, every append takes the
    /// partition's counter lock until it commits.
    /// </summary>
    public bool MaintainPartitionCounter { get; set; } = true;
}
