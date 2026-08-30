using Microsoft.EntityFrameworkCore;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Standalone EF Core context hosting the Stratara identity-directory tables
/// (<c>tenant_membership</c>, <c>active_tenant</c>, <c>setting_entry</c>, <c>api_key</c>) for hosts that do not
/// fold them into an existing context. Consumers derive a concrete context and own its migrations, mirroring the
/// pattern of the ASP.NET Identity store base:
/// <c>class MyDirectoryDbContext(DbContextOptions&lt;MyDirectoryDbContext&gt; options)
/// : IdentityDirectoryDbContext&lt;MyDirectoryDbContext&gt;(options)</c>.
/// </summary>
/// <remarks>
/// The model is applied via explicit <c>ApplyConfiguration</c> calls
/// (<see cref="IdentityDirectoryModelBuilderExtensions.ApplyIdentityDirectoryModel"/>), not an
/// assembly scan, so co-hosting this context next to other contexts can never leak foreign
/// entity configurations into its model. To add the directory tables to an <em>existing</em>
/// context instead (single migration lineage), skip this base class and call
/// <see cref="IdentityDirectoryModelBuilderExtensions.ApplyIdentityDirectoryModel"/> from that
/// context's <c>OnModelCreating</c>.
/// </remarks>
/// <typeparam name="TContext">The concrete derived DbContext type (used for <see cref="DbContextOptions{TContext}"/> binding).</typeparam>
/// <param name="options">Options bound by the host's DbContext registration.</param>
public class IdentityDirectoryDbContext<TContext>(DbContextOptions<TContext> options)
    : DbContext(options) where TContext : DbContext
{
    /// <summary>The user↔tenant membership rows.</summary>
    public DbSet<TenantMembershipEntry> TenantMemberships => Set<TenantMembershipEntry>();

    /// <summary>The per-user active-tenant selections.</summary>
    public DbSet<ActiveTenantEntry> ActiveTenants => Set<ActiveTenantEntry>();

    /// <summary>The scoped setting values.</summary>
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    /// <summary>The issued API keys (hashed).</summary>
    public DbSet<ApiKeyEntry> ApiKeys => Set<ApiKeyEntry>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyIdentityDirectoryModel();
    }
}
