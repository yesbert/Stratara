using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;

/// <summary>EF Core mapping for the table of deleted tenants, one row per projection and tenant.</summary>
[ExcludeFromCodeCoverage]
internal sealed class ForgottenTenantConfiguration : IEntityTypeConfiguration<ForgottenTenant>
{
    /// <summary>The table of deleted tenants.</summary>
    public const string Table = "projection_forgotten_tenant";

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ForgottenTenant> b)
    {
        b.ToTable(Table);
        b.HasKey(forgotten => new { forgotten.Projection, forgotten.TenantId });
        b.Property(forgotten => forgotten.Projection).HasMaxLength(255);
    }
}
