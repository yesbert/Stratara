using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stratara.Abstractions.EventSourcing;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Configurations;

/// <summary>
/// EF Core mapping for the event-chain anchor table. Enforces a unique
/// <c>(BucketId, SequenceNumber)</c> pair so each bucket's hash chain is single-writer, and indexes the
/// sequence number alone so the latest anchor is found without sorting every anchor.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class EventChainAnchorConfiguration : IEntityTypeConfiguration<EventChainAnchor>
{
    /// <summary>The index on the anchors' sequence number.</summary>
    public const string SequenceNumberIndex = "ix_event_chain_anchor_sequence_number";

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<EventChainAnchor> b)
    {
        b.HasIndex(e => new { e.BucketId, e.SequenceNumber }).IsUnique();
        b.HasIndex(e => e.SequenceNumber, SequenceNumberIndex).HasDatabaseName(SequenceNumberIndex);
    }
}
