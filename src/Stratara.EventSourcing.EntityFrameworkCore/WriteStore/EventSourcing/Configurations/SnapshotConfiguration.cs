using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stratara.Shared.EventSourcing;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Configurations;

/// <summary>
/// EF Core mapping for the <c>snapshot</c> table. Enforces a unique
/// <c>(BucketId, StreamId, Version)</c> index so each aggregate snapshot lands once per version
/// within its bucket and constrains the aggregate-type-name length.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SnapshotConfiguration : IEntityTypeConfiguration<Snapshot>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Snapshot> b)
    {
        b.HasIndex(e => new { e.BucketId, e.StreamId, e.Version }).IsUnique();
        b.Property(e => e.AggregateTypeName).IsRequired().HasMaxLength(255);
    }
}
