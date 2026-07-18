using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stratara.Identity.EntityFrameworkCore.Configurations;

/// <summary>
/// EF Core mapping for the <c>active_tenant</c> table: primary key on <c>UserId</c> so each
/// user has at most one active-tenant selection.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class ActiveTenantEntryConfiguration : IEntityTypeConfiguration<ActiveTenantEntry>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ActiveTenantEntry> b)
    {
        b.ToTable("active_tenant");
        b.HasKey(e => e.UserId);
    }
}
