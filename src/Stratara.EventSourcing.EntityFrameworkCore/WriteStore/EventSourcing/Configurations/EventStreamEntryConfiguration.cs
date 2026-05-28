using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stratara.Shared.EventSourcing;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Configurations;

/// <summary>
/// EF Core mapping for the <c>event_stream_entry</c> table backing
/// <see cref="EventStreamEntry"/>. Uses <c>SequenceNumber</c> as the surrogate primary key
/// (database-generated) and enforces a unique <c>(BucketId, StreamId, Version)</c> index so
/// per-stream version numbers stay monotonic per bucket.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class EventStreamEntryConfiguration : IEntityTypeConfiguration<EventStreamEntry>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<EventStreamEntry> b)
    {
        b.HasKey(e => e.SequenceNumber);
        b.Property(e => e.SequenceNumber).ValueGeneratedOnAdd();
        b.HasIndex(e => new { e.BucketId, e.StreamId, e.Version }).IsUnique();
        b.Property(e => e.CausationId).IsRequired().HasMaxLength(128);
        b.Property(e => e.CorrelationId).IsRequired().HasMaxLength(128);
        b.Property(e => e.EventTypeName).IsRequired().HasMaxLength(255);
        b.Property(e => e.AggregateTypeName).IsRequired().HasMaxLength(255);
    }
}
