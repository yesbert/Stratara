using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;

/// <summary>EF Core mapping for the per-partition counter table the portable commit-order reader orders by.</summary>
[ExcludeFromCodeCoverage]
internal sealed class PartitionPositionConfiguration : IEntityTypeConfiguration<PartitionPosition>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<PartitionPosition> b)
    {
        b.ToTable(CommitOrderSchema.PartitionPositionTable);
        b.HasKey(counter => counter.Partition);
        b.Property(counter => counter.Partition).ValueGeneratedNever();
    }
}
