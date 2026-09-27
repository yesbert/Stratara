using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// Scenario <em>The write context declares the tenant filter</em>, for the portable reader and its start check on
/// SQLite. The write context filters every tenant-scoped entity to its ambient tenant, as the tenant-isolation guide
/// switches the filter on, and has no session, so a query through it sees no tenant's entries. The reader still returns
/// the entries of both tenants up to its head, and a host whose store holds an unpositioned entry of either tenant
/// does not start.
/// </summary>
public sealed class TenantFilteredPortableReaderTests : IAsyncDisposable
{
    private const int BucketId = 8;
    private const int PartitionCount = 4;
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    [Fact]
    public async Task The_portable_reader_returns_every_tenants_entries()
    {
        var positioned = await CreateStoreAsync(withCounter: true);
        await AppendAsync(positioned, Guid.NewGuid(), Guid.NewGuid());
        var reader = new PortableCounterReader<TenantFilteredWriteDbContext>(new Factory(positioned), Options.Create(new CommitOrderOptions { PartitionCount = PartitionCount }));
        var partition = BucketId % PartitionCount;

        var batch = await reader.ReadAfterAsync(partition, 0, 10, TestContext.Current.CancellationToken);

        Assert.Equal(2, batch.Entries.Select(entry => entry.Entry.TenantId).Distinct().Count());
        Assert.Equal([1L, 2L], batch.Entries.Select(entry => entry.Position));
        Assert.Equal(2L, await reader.HeadAsync(partition, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_start_check_refuses_an_unpositioned_entry_of_any_tenant()
    {
        var positioned = await CreateStoreAsync(withCounter: true);
        await AppendAsync(positioned, Guid.NewGuid());
        await AppendAsync(ContextOptions(withCounter: false), Guid.NewGuid());

        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services
            .Configure<CommitOrderOptions>(o => o.PartitionCount = PartitionCount)
            .AddSingleton<IDbContextFactory<TenantFilteredWriteDbContext>>(new Factory(positioned))
            .AddStrataraPortableCounterReader<TenantFilteredWriteDbContext>();
        using var host = builder.Build();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(nameof(PartitionCounterBackfill), refused.Message, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private async Task<DbContextOptions<TenantFilteredWriteDbContext>> CreateStoreAsync(bool withCounter)
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = ContextOptions(withCounter);
        await using var context = new TenantFilteredWriteDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.Set<PartitionPosition>().AddRange(Enumerable.Range(0, PartitionCount).Select(partition => new PartitionPosition { Partition = partition, Position = 0 }));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return options;
    }

    private DbContextOptions<TenantFilteredWriteDbContext> ContextOptions(bool withCounter)
    {
        var builder = new DbContextOptionsBuilder<TenantFilteredWriteDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, SqliteTimeModelCustomizer>();
        if (withCounter)
        {
            builder.AddInterceptors(new PartitionCounterInterceptor(Options.Create(new CommitOrderOptions { PartitionCount = PartitionCount })));
        }

        return builder.Options;
    }

    private static async Task AppendAsync(DbContextOptions<TenantFilteredWriteDbContext> options, params Guid[] tenants)
    {
        foreach (var tenantId in tenants)
        {
            await using var context = new TenantFilteredWriteDbContext(options);
            context.Set<EventStreamEntry>().Add(new EventStreamEntry
            {
                Id = Guid.CreateVersion7(),
                StreamId = Guid.NewGuid(),
                Version = 1,
                EventTypeName = "Probe",
                AggregateTypeName = "ProbeAggregate",
                DataJson = "{}",
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.CreateVersion7().ToString("N"),
                CausationId = Guid.CreateVersion7().ToString("N"),
                BucketId = BucketId,
                TenantId = tenantId,
                ActorTenantId = tenantId,
                ActorUserId = tenantId,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class Factory(DbContextOptions<TenantFilteredWriteDbContext> options) : IDbContextFactory<TenantFilteredWriteDbContext>
    {
        public TenantFilteredWriteDbContext CreateDbContext() => new(options);
    }

    /// <summary>
    /// A write context that filters every tenant-scoped entity to its ambient tenant, the way the tenant-isolation guide
    /// switches the filter on. It has no session, so its ambient tenant is the empty identifier.
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
}
