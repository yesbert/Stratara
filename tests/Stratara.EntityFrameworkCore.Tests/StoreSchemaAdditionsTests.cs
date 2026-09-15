using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests;

/// <summary>
/// The framework's write and read models declare what the commit-order readers and the execution
/// model need, so a consumer's migration produces them without any opt-in: the position column and
/// its counter table, the transaction-id column on PostgreSQL, the outbox record's resume bookkeeping
/// and the checkpoint table, each with the index its reader uses.
/// </summary>
public sealed class StoreSchemaAdditionsTests
{
    [Fact]
    public void The_write_model_on_PostgreSQL_declares_the_commit_order_columns_and_their_indexes()
    {
        using var context = new SchemaWriteContext(new DbContextOptionsBuilder<SchemaWriteContext>()
            .UseNpgsql("Host=unused")
            .UseSnakeCaseNamingConvention()
            .Options);
        var entry = Entity<EventStreamEntry>(context.Model);
        var table = StoreObjectIdentifier.Table(entry.GetTableName()!, entry.GetSchema());

        var transactionId = entry.FindProperty(CommitOrderSchema.TransactionIdColumn);
        Assert.NotNull(transactionId);
        Assert.Equal("xid8", transactionId.GetColumnType());
        Assert.Equal("pg_current_xact_id()", transactionId.GetDefaultValueSql());
        Assert.Equal(CommitOrderSchema.TransactionIdColumn, transactionId.GetColumnName(table));

        var position = entry.FindProperty(CommitOrderSchema.PartitionPositionColumn);
        Assert.NotNull(position);
        Assert.True(position.IsNullable);
        Assert.Equal(CommitOrderSchema.PartitionPositionColumn, position.GetColumnName(table));

        Assert.Contains(entry.GetIndexes(), index => index.Properties.Single().Name == CommitOrderSchema.TransactionIdColumn);
        Assert.Contains(entry.GetIndexes(), index => index.Properties.Single().Name == CommitOrderSchema.PartitionPositionColumn);
    }

    [Fact]
    public void The_write_model_declares_the_partition_counter_table()
    {
        using var context = InMemoryWriteContext();
        var counter = Entity<PartitionPosition>(context.Model);

        Assert.Equal(CommitOrderSchema.PartitionPositionTable, counter.GetTableName());
        Assert.Equal([nameof(PartitionPosition.Partition)], counter.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(ValueGenerated.Never, counter.FindProperty(nameof(PartitionPosition.Partition))!.ValueGenerated);
    }

    [Fact]
    public void The_write_model_on_another_provider_declares_the_position_but_not_the_transaction_id()
    {
        using var context = InMemoryWriteContext();
        var entry = Entity<EventStreamEntry>(context.Model);

        Assert.NotNull(entry.FindProperty(CommitOrderSchema.PartitionPositionColumn));
        Assert.Null(entry.FindProperty(CommitOrderSchema.TransactionIdColumn));
    }

    [Theory]
    [InlineData(nameof(OutboxEntry.AttemptCount))]
    [InlineData(nameof(OutboxEntry.LastHandedOverAt))]
    [InlineData(nameof(OutboxEntry.KeptAt))]
    [InlineData(nameof(OutboxEntry.LastFailure))]
    [InlineData(nameof(OutboxEntry.AggregateId))]
    [InlineData(nameof(OutboxEntry.Heavy))]
    public void The_write_model_declares_the_outbox_resume_bookkeeping(string property)
    {
        using var context = InMemoryWriteContext();

        Assert.NotNull(Entity<OutboxEntry>(context.Model).FindProperty(property));
    }

    [Fact]
    public void The_read_model_declares_the_checkpoint_table_keyed_by_consumer_and_partition()
    {
        using var context = new SchemaReadContext(new DbContextOptionsBuilder<SchemaReadContext>()
            .UseInMemoryDatabase($"schema-read-{Guid.NewGuid():N}")
            .Options);
        var checkpoint = Entity<ProjectionCheckpoint>(context.Model);

        Assert.Equal("projection_checkpoint", checkpoint.GetTableName());
        Assert.Equal(
            [nameof(ProjectionCheckpoint.Projection), nameof(ProjectionCheckpoint.Partition)],
            checkpoint.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(255, checkpoint.FindProperty(nameof(ProjectionCheckpoint.Reader))!.GetMaxLength());
    }

    [Fact]
    public void The_read_model_carries_no_write_side_addition_and_the_write_model_no_checkpoint()
    {
        using var read = new SchemaReadContext(new DbContextOptionsBuilder<SchemaReadContext>()
            .UseInMemoryDatabase($"schema-read-{Guid.NewGuid():N}")
            .Options);
        using var write = InMemoryWriteContext();

        Assert.Null(read.Model.FindEntityType(typeof(PartitionPosition)));
        Assert.Null(write.Model.FindEntityType(typeof(ProjectionCheckpoint)));
    }

    private static IEntityType Entity<T>(IModel model) =>
        model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not in the model.");

    private static SchemaWriteContext InMemoryWriteContext() =>
        new(new DbContextOptionsBuilder<SchemaWriteContext>()
            .UseInMemoryDatabase($"schema-write-{Guid.NewGuid():N}")
            .Options);

    private sealed class SchemaWriteContext(DbContextOptions<SchemaWriteContext> options)
        : WriteDbContext<SchemaWriteContext>(options);

    private sealed class SchemaReadContext(DbContextOptions<SchemaReadContext> options)
        : ReadDbContext<SchemaReadContext>(options);
}
