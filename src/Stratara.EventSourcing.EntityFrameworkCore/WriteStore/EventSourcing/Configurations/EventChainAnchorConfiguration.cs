using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stratara.Abstractions.EventSourcing;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Configurations;

/// <summary>
/// EF Core mapping for the event-chain anchor table. Enforces a unique
/// <c>(BucketId, SequenceNumber)</c> pair so each bucket's hash chain is single-writer.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class EventChainAnchorConfiguration : IEntityTypeConfiguration<EventChainAnchor>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<EventChainAnchor> b)
    {
        b.HasIndex(e => new { e.BucketId, e.SequenceNumber }).IsUnique();
    }
}
