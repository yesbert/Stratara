using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Testing.EntityFrameworkCore;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// The portable reader's backfill on SQLite, which positions a batch one entry at a time rather than with the
/// statement PostgreSQL uses. Two streams are appended in reverse version order, one save per entry, so their
/// sequence numbers run against their versions — one early in the partition, one where the first batch would end
/// between its versions.
/// </summary>
public sealed class SqlitePartitionCounterBackfillTests
{
    private const int BucketId = 8;
    private const int Fillers = 995;

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
        var commitOrder = new CommitOrderOptions { PartitionCount = 4 };

        Assert.Equal(Fillers + 6, await PartitionCounterBackfill.RunAsync(context, commitOrder, TestContext.Current.CancellationToken));

        var positioned = await context.Set<EventStreamEntry>().AsNoTracking()
            .Select(e => new { e.StreamId, e.Version, Position = EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) })
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L, 3L], positioned.Where(e => e.StreamId == early).OrderBy(e => e.Position).Select(e => e.Version));
        Assert.Equal([1L, 2L, 3L], positioned.Where(e => e.StreamId == straddling).OrderBy(e => e.Position).Select(e => e.Version));
        Assert.Equal(Enumerable.Range(1, Fillers + 6).Select(i => (long?)i), positioned.Select(e => e.Position).Order());
    }

    private static async Task AppendAsync(StrataraTestWriteDbContext context, params (Guid Stream, long Version)[] entries)
    {
        var tenantId = Guid.NewGuid();
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
}
