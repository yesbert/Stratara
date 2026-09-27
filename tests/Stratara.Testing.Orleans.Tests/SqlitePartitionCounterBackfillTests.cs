using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Testing.EntityFrameworkCore;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// The portable reader's backfill on SQLite, which positions a batch one entry at a time rather than with the
/// statement PostgreSQL uses. Two streams are appended in reverse version order, one save per entry, so their
/// sequence numbers run against their versions — one early in the partition, one where the first batch would end
/// between its versions. A batch covers the store's next thousand entries per partition, so with every entry in one
/// partition the first batch ends inside the second stream.
/// </summary>
public sealed class SqlitePartitionCounterBackfillTests
{
    private const int BucketId = 8;
    private const int PartitionCount = 4;
    private const int Fillers = 1_000 * PartitionCount - 5;

    [Fact]
    public async Task Inverted_streams_are_positioned_in_version_order_with_and_without_a_batch_boundary_between_them()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<StrataraTestWriteDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCustomizer, SqliteTimeModelCustomizer>()
            .Options;
        await using var context = new StrataraTestWriteDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var early = Guid.NewGuid();
        var straddling = Guid.NewGuid();
        await AppendAsync(context, (early, 3), (early, 2), (early, 1));
        await AppendAsync(context, Enumerable.Range(0, Fillers).Select(_ => (Guid.NewGuid(), 1L)).ToArray());
        await AppendAsync(context, (straddling, 3), (straddling, 2), (straddling, 1));
        var commitOrder = new CommitOrderOptions { PartitionCount = PartitionCount };

        Assert.Equal(Fillers + 6, await PartitionCounterBackfill.RunAsync(context, commitOrder, TestContext.Current.CancellationToken));

        var positioned = await context.Set<EventStreamEntry>().AsNoTracking()
            .Select(e => new { e.StreamId, e.Version, Position = EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) })
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L, 3L], positioned.Where(e => e.StreamId == early).OrderBy(e => e.Position).Select(e => e.Version));
        Assert.Equal([1L, 2L, 3L], positioned.Where(e => e.StreamId == straddling).OrderBy(e => e.Position).Select(e => e.Version));
        Assert.Equal(Enumerable.Range(1, Fillers + 6).Select(i => (long?)i), positioned.Select(e => e.Position).Order());
    }

    /// <summary>
    /// Scenario <em>The write context filters entries by tenant</em>, for the positioning every provider but PostgreSQL
    /// uses: the entries of two tenants, one tenant's stream numbered against its versions, in a store whose write
    /// context hides every tenant's entries from a query without a session. The backfill positions all of them, in
    /// append order with the inverted stream in version order.
    /// </summary>
    [Fact]
    public async Task A_write_context_that_filters_entries_by_tenant_has_every_tenants_entries_positioned()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<TenantFilteredWriteDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCustomizer, SqliteTimeModelCustomizer>()
            .Options;
        await using var context = new TenantFilteredWriteDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var inverted = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await AppendAsync(context, first, (Guid.NewGuid(), 1L), (inverted, 2L), (inverted, 1L));
        await AppendAsync(context, second, (Guid.NewGuid(), 1L));

        Assert.Empty(await context.Set<EventStreamEntry>().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(4, await PartitionCounterBackfill.RunAsync(context, new CommitOrderOptions { PartitionCount = PartitionCount }, TestContext.Current.CancellationToken));

        var positioned = await context.Set<EventStreamEntry>().IgnoreQueryFilters().AsNoTracking()
            .OrderBy(e => e.SequenceNumber)
            .Select(e => new { e.StreamId, e.Version, e.TenantId, Position = EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) })
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1L, 3L, 2L, 4L], positioned.Select(e => e.Position));
        Assert.Equal([1L, 2L], positioned.Where(e => e.StreamId == inverted).OrderBy(e => e.Position).Select(e => e.Version));
        Assert.Equal([first, second], positioned.Select(e => e.TenantId).Distinct());
    }

    private static Task AppendAsync(DbContext context, params (Guid Stream, long Version)[] entries) =>
        AppendAsync(context, Guid.NewGuid(), entries);

    private static async Task AppendAsync(DbContext context, Guid tenantId, params (Guid Stream, long Version)[] entries)
    {
        foreach (var (stream, version) in entries)
        {
            context.Set<EventStreamEntry>().Add(new EventStreamEntry
            {
                Id = Guid.CreateVersion7(),
                StreamId = stream,
                Version = version,
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

        context.ChangeTracker.Clear();
    }

    /// <summary>
    /// A write context that filters every tenant-scoped entity to its ambient tenant, the way the tenant-isolation guide
    /// switches the filter on. It has no session, so its ambient tenant is the empty identifier and a query through it
    /// sees no tenant's entries — which is how a migration step that runs before the host sees the store.
    /// </summary>
    private sealed class TenantFilteredWriteDbContext(DbContextOptions<TenantFilteredWriteDbContext> options)
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
