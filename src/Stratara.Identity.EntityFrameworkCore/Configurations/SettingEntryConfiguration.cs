using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stratara.Identity.EntityFrameworkCore.Configurations;

/// <summary>
/// EF Core mapping for the <c>setting_entry</c> table. Composite key
/// <c>(Name, TenantId, UserId)</c> — unset scope dimensions are empty strings, keeping the key
/// portable — plus per-dimension indexes for the erasure sweeps.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SettingEntryConfiguration : IEntityTypeConfiguration<SettingEntry>
{
    private const int NameMaxLength = 128;
    private const int ScopeIdMaxLength = 64;

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<SettingEntry> b)
    {
        b.ToTable("setting_entry");
        b.HasKey(e => new { e.Name, e.TenantId, e.UserId });

        b.Property(e => e.Name).IsRequired().HasMaxLength(NameMaxLength);
        b.Property(e => e.TenantId).IsRequired().HasMaxLength(ScopeIdMaxLength);
        b.Property(e => e.UserId).IsRequired().HasMaxLength(ScopeIdMaxLength);
        b.Property(e => e.Value).IsRequired();

        b.HasIndex(e => e.TenantId);
        b.HasIndex(e => e.UserId);
    }
}
