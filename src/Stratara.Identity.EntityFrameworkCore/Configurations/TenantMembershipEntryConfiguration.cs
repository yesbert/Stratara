using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stratara.Identity.EntityFrameworkCore.Configurations;

/// <summary>
/// EF Core mapping for the <c>tenant_membership</c> table. Composite key
/// <c>(UserId, TenantId)</c>, an index on <c>TenantId</c> for the reverse lookup
/// ("who belongs to this tenant"), roles as a JSON string column, and the status enum
/// stored by name so persisted values survive enum reordering.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class TenantMembershipEntryConfiguration : IEntityTypeConfiguration<TenantMembershipEntry>
{
    private const int StatusMaxLength = 32;

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<TenantMembershipEntry> b)
    {
        b.ToTable("tenant_membership");
        b.HasKey(e => new { e.UserId, e.TenantId });
        b.HasIndex(e => e.TenantId);

        b.Property(e => e.Roles)
            .IsRequired()
            .HasConversion(
                roles => JsonSerializer.Serialize(roles, JsonSerializerOptions.Default),
                json => JsonSerializer.Deserialize<List<string>>(json, JsonSerializerOptions.Default) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
                    roles => roles.Aggregate(0, (hash, role) => HashCode.Combine(hash, role.GetHashCode())),
                    roles => roles.ToList()));

        b.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(StatusMaxLength);
    }
}
