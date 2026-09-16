using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;
using Stratara.Orleans.EntityFrameworkCore.Projections;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Two writers of the same first checkpoint — an activation and its successor overlapping during a
/// failover — both find no row and both insert. The one that loses to the key takes the row over
/// instead of failing.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CheckpointFirstWriteTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_checkpoint_first_write";

    [Fact]
    public async Task A_first_write_that_loses_the_insert_to_another_writer_updates_the_row_instead_of_failing()
    {
        var connectionString = postgres.ConnectionStringFor(Database);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(connectionString);
        var plain = new ContextFactory(connectionString);
        await using (var context = plain.CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        var projection = $"first-write-{Guid.NewGuid():N}";
        var racing = new ContextFactory(connectionString, new InsertFirst(plain, projection));
        var store = new ProjectionCheckpointStore<PocReadDbContext>(racing);

        await store.SetAsync(projection, 3, "partition-counter/16", 42, TestContext.Current.CancellationToken);

        Assert.Equal(42, await new ProjectionCheckpointStore<PocReadDbContext>(plain).GetAsync(projection, 3, "partition-counter/16", TestContext.Current.CancellationToken));
    }

    /// <summary>Inserts the checkpoint through another context just before the store's own insert is saved.</summary>
    private sealed class InsertFirst(ContextFactory other, string projection) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await using var context = other.CreateDbContext();
            context.Set<ProjectionCheckpoint>().Add(new ProjectionCheckpoint { Projection = projection, Partition = 3, Position = 7, Reader = "partition-counter/16" });
            await context.SaveChangesAsync(cancellationToken);
            return result;
        }
    }

    private sealed class ContextFactory(string connectionString, params IInterceptor[] interceptors) : IDbContextFactory<PocReadDbContext>
    {
        public PocReadDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<PocReadDbContext>()
                .UseSnakeCaseNamingConvention()
                .UseNpgsql(connectionString)
                .AddInterceptors(interceptors)
                .Options);
    }
}
