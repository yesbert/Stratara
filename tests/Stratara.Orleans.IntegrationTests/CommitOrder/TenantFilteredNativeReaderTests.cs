using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// Scenario <em>The write context declares the tenant filter</em>, for the native reader on PostgreSQL. The write
/// context filters every tenant-scoped entity to its ambient tenant, as the tenant-isolation guide switches the filter
/// on, and has no session, so a query through it sees no tenant's entries. The reader's statement is raw SQL that the
/// filter would otherwise be composed over; it still returns the entries of both tenants.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class TenantFilteredNativeReaderTests(PostgreSqlFixture postgres)
{
    private const int BucketId = 8;

    [Fact]
    public async Task The_native_reader_returns_every_tenants_entries()
    {
        await using var store = await PocStore<TenantFilteredWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor("poc_native_reader_tenant_filtered"), maintainCounter: false);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Set<EventStreamEntry>().IgnoreQueryFilters().ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var tenants = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var tenantId in tenants)
        {
            await using var context = await store.CreateContextAsync();
            context.Set<EventStreamEntry>().Add(PocStore<TenantFilteredWriteDbContext>.NewEntry(Guid.NewGuid(), 1, BucketId, tenantId));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var reader = new PostgresTransactionIdReader<TenantFilteredWriteDbContext>(store.ContextFactory, Options.Create(store.Options));
        var partition = PartitionMap.PartitionOf(BucketId, store.Options.PartitionCount);

        var batch = await reader.ReadAfterAsync(partition, 0, 10, TestContext.Current.CancellationToken);

        Assert.Equal(tenants.Order(), batch.Entries.Select(entry => entry.Entry.TenantId).Order());
    }
}
