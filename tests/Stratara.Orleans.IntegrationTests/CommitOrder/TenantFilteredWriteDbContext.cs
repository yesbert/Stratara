using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// A write context that filters every tenant-scoped entity to its ambient tenant, the way the tenant-isolation guide
/// switches the filter on. It has no session, so its ambient tenant is the empty identifier and a query through it sees
/// no tenant's entries — which is how a migration step that runs before the host sees the store.
/// </summary>
public sealed class TenantFilteredWriteDbContext(DbContextOptions<TenantFilteredWriteDbContext> options)
    : WriteDbContext<TenantFilteredWriteDbContext>(options), ITenantScopedDbContext
{
    public Guid TenantId => Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyGlobalTenantQueryFilters(this);
    }
}
