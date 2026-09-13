namespace Stratara.Orleans.CommitOrder;

/// <summary>
/// Settings shared by every <see cref="ICommittedPositionReader"/>.
/// </summary>
public sealed class CommitOrderOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Orleans:CommitOrder";

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

    /// <summary>
    /// How much older than the moment of reading an entry must be before the safety-window baseline
    /// returns it. A transaction that stays open longer than this defeats the baseline, which is the
    /// point of measuring it.
    /// </summary>
    public TimeSpan SafetyWindow { get; set; } = TimeSpan.FromMilliseconds(100);
}
