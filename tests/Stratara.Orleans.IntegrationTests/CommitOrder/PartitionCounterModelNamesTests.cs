using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// The partition counter advances the counter row through the context's model, so a consumer whose
/// model does not follow the snake-case convention appends and reads as a conventional one does.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PartitionCounterModelNamesTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_partition_counter_pascal";

    [Fact]
    public async Task The_partition_counter_positions_appends_to_a_store_whose_names_are_not_snake_case()
    {
        await using var store = await PocStore<PascalCaseCounterWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(Database),
            maintainCounter: true);
        var reader = new PortableCounterReader<PascalCaseCounterWriteDbContext>(store.ContextFactory, Options.Create(store.Options));

        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var before = (await reader.ReadAfterAsync(partition, 0, int.MaxValue - 1, TestContext.Current.CancellationToken)).Position;
        var streamId = Guid.NewGuid();
        var written = new[]
        {
            PocStore<PascalCaseCounterWriteDbContext>.NewEntry(streamId, 1, bucketId, Guid.NewGuid()),
            PocStore<PascalCaseCounterWriteDbContext>.NewEntry(streamId, 2, bucketId, Guid.NewGuid()),
        };
        await using (var context = await store.CreateContextAsync())
        {
            context.Set<EventStreamEntry>().AddRange(written);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var batch = await reader.ReadAfterAsync(partition, before, 100, TestContext.Current.CancellationToken);

        Assert.Equal(written.Select(entry => entry.Id), batch.Entries.Select(entry => entry.Entry.Id));
        Assert.Equal([before + 1, before + 2], batch.Entries.Select(entry => entry.Position));
    }
}

/// <summary>
/// The shipped write model with the partition counter and the stamped column renamed away from the
/// snake-case convention, and the counter maintained.
/// </summary>
public sealed class PascalCaseCounterWriteDbContext(
    DbContextOptions<PascalCaseCounterWriteDbContext> options,
    IOptions<CommitOrderOptions> commitOrder)
    : WriteDbContext<PascalCaseCounterWriteDbContext>(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.AddInterceptors(new PartitionCounterInterceptor(commitOrder));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var counter = modelBuilder.Entity<PartitionPosition>();
        counter.ToTable("PartitionPositions");
        counter.Property(c => c.Partition).HasColumnName("Partition");
        counter.Property(c => c.Position).HasColumnName("Position");
        modelBuilder.Entity<EventStreamEntry>().Property<long?>(CommitOrderSchema.PartitionPositionColumn).HasColumnName("PartitionPosition");
    }
}
