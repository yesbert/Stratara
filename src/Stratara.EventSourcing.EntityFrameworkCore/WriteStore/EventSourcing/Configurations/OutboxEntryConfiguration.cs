using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stratara.Abstractions.Outbox;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Configurations;

/// <summary>
/// EF Core mapping for the <c>outbox_entry</c> table. Indexes <c>BucketId</c> so the outbox
/// worker can scan its assigned partition without a full-table seek.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class OutboxEntryConfiguration : IEntityTypeConfiguration<OutboxEntry>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<OutboxEntry> b)
    {
        b.HasIndex(e => e.BucketId);
    }
}
