using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;

/// <summary>
/// One row per partition: the last position handed out to an entry in that partition. Incremented
/// inside the transaction that appends the entry, so that commit order and position order agree.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class PartitionPosition
{
    /// <summary>The partition this counter belongs to.</summary>
    public int Partition { get; set; }

    /// <summary>The last position handed out.</summary>
    public long Position { get; set; }
}
