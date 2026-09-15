using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;

/// <summary>EF Core mapping for the checkpoint table, one row per consumer and partition.</summary>
[ExcludeFromCodeCoverage]
internal sealed class ProjectionCheckpointConfiguration : IEntityTypeConfiguration<ProjectionCheckpoint>
{
    /// <summary>The checkpoint table.</summary>
    public const string Table = "projection_checkpoint";

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ProjectionCheckpoint> b)
    {
        b.ToTable(Table);
        b.HasKey(checkpoint => new { checkpoint.Projection, checkpoint.Partition });
        b.Property(checkpoint => checkpoint.Projection).HasMaxLength(255);
        b.Property(checkpoint => checkpoint.Reader).HasMaxLength(255);
    }
}
