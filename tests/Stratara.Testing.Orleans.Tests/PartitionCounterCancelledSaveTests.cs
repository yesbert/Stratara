using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Testing.EntityFrameworkCore;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// A save that appends and is cancelled while its entries are written leaves no transaction of the partition counter
/// open on its context: the context's next save runs in a transaction of its own and commits.
/// </summary>
public sealed class PartitionCounterCancelledSaveTests
{
    private const int PartitionCount = 4;

    /// <summary>Requests the cancellation the moment the entries are inserted — once, for the first save.</summary>
    private sealed class CancelsTheFirstInsert(CancellationTokenSource stop) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase) && !stop.IsCancellationRequested)
            {
                await stop.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return result;
        }
    }

    [Fact]
    public async Task A_cancelled_append_leaves_no_transaction_open_for_the_next_save()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var seeding = new StrataraTestWriteDbContext(new DbContextOptionsBuilder<StrataraTestWriteDbContext>()
                         .UseSqlite(connection)
                         .ReplaceService<IModelCustomizer, SqliteTimeModelCustomizer>()
                         .Options))
        {
            await seeding.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            seeding.Set<PartitionPosition>().AddRange(Enumerable.Range(0, PartitionCount).Select(partition => new PartitionPosition { Partition = partition }));
            await seeding.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var stop = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<StrataraTestWriteDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCustomizer, SqliteTimeModelCustomizer>()
            .AddInterceptors(new PartitionCounterInterceptor(Options.Create(new CommitOrderOptions { PartitionCount = PartitionCount })), new CancelsTheFirstInsert(stop))
            .Options;
        await using var context = new StrataraTestWriteDbContext(options);

        context.Set<EventStreamEntry>().Add(Entry());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.SaveChangesAsync(stop.Token));

        Assert.Null(context.Database.CurrentTransaction);
        context.ChangeTracker.Clear();
        context.Set<EventStreamEntry>().Add(Entry());
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await context.Set<EventStreamEntry>().CountAsync(TestContext.Current.CancellationToken));
    }

    private static async Task SeedAsync(SqliteConnection connection)
    {
        await using var seeding = new StrataraTestWriteDbContext(new DbContextOptionsBuilder<StrataraTestWriteDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCustomizer, SqliteTimeModelCustomizer>()
            .Options);
        await seeding.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        seeding.Set<PartitionPosition>().AddRange(Enumerable.Range(0, PartitionCount).Select(partition => new PartitionPosition { Partition = partition }));
        await seeding.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static EventStreamEntry Entry()
    {
        var tenantId = Guid.NewGuid();
        return new EventStreamEntry
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
            BucketId = 8,
            TenantId = tenantId,
            ActorTenantId = tenantId,
            ActorUserId = tenantId,
        };
    }
}
