using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Entities;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Configurations;

/// <summary>
/// EF Core mapping for the <c>command_log_entry</c> table backing <see cref="CommandLogEntry"/>:
/// indexes the bucket id and pins lengths on the correlation/causation/type-name columns.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class CommandLogEntryConfiguration : IEntityTypeConfiguration<CommandLogEntry>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<CommandLogEntry> b)
    {
        b.HasIndex(e => e.BucketId);
        b.Property(e => e.CausationId).IsRequired().HasMaxLength(128);
        b.Property(e => e.CorrelationId).IsRequired().HasMaxLength(128);
        b.Property(e => e.CommandTypeName).IsRequired().HasMaxLength(255);
    }
}
