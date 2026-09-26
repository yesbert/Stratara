using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.EventSourcing.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Testing.EntityFrameworkCore;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// <c>orleans-execution</c> → <em>A write context retries on failure</em>: an append through the framework's unit of
/// work, on a write context whose execution strategy retries on failure and that keeps the partition positions, is
/// saved and positioned. Before, the partition counter began a transaction outside the strategy, which a retrying
/// strategy refuses, and every append failed.
/// </summary>
public sealed class PartitionCounterRetryingStrategyTests
{
    private const int PartitionCount = 4;

    private sealed class RetryingStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(10))
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    private sealed class Factory(DbContextOptions<StrataraTestWriteDbContext> options) : IDbContextFactory<StrataraTestWriteDbContext>
    {
        public StrataraTestWriteDbContext CreateDbContext() => new(options);
    }

    private sealed class TestUnitOfWork(IDbContextFactory<StrataraTestWriteDbContext> factory) : UnitOfWork<StrataraTestWriteDbContext>(factory)
    {
        public static StrataraTestWriteDbContext ContextOf(ITransaction transaction) => GetDbContext(transaction);
    }

    [Fact]
    public async Task An_append_under_a_retrying_strategy_is_saved_and_positioned()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await SeedAsync(connection);
        var unitOfWork = new TestUnitOfWork(new Factory(new DbContextOptionsBuilder<StrataraTestWriteDbContext>()
            .UseSqlite(connection, sqlite => sqlite.ExecutionStrategy(dependencies => new RetryingStrategy(dependencies)))
            .ReplaceService<IModelCustomizer, SqliteTimeModelCustomizer>()
            .AddInterceptors(new PartitionCounterInterceptor(Options.Create(new CommitOrderOptions { PartitionCount = PartitionCount })), CommitCompletionInterceptor.Instance)
            .Options));

        await using (var transaction = await unitOfWork.StartAsync(TestContext.Current.CancellationToken))
        {
            TestUnitOfWork.ContextOf(transaction).Set<EventStreamEntry>().Add(Entry());

            await transaction.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = new StrataraTestWriteDbContext(new DbContextOptionsBuilder<StrataraTestWriteDbContext>()
            .UseSqlite(connection).ReplaceService<IModelCustomizer, SqliteTimeModelCustomizer>().Options);
        var position = await read.Set<EventStreamEntry>().AsNoTracking()
            .Select(entry => EF.Property<long?>(entry, CommitOrderSchema.PartitionPositionColumn))
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1L, position);
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
