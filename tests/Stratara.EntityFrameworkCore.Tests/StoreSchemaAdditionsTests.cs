using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;
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
    [InlineData(nameof(OutboxEntry.ConflictCount))]
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
    public void The_read_model_declares_the_forgotten_tenant_table_keyed_by_projection_and_tenant()
    {
        using var context = new SchemaReadContext(new DbContextOptionsBuilder<SchemaReadContext>()
            .UseInMemoryDatabase($"schema-read-{Guid.NewGuid():N}")
            .Options);
        var forgotten = Entity<ForgottenTenant>(context.Model);

        Assert.Equal("projection_forgotten_tenant", forgotten.GetTableName());
        Assert.Equal(
            [nameof(ForgottenTenant.Projection), nameof(ForgottenTenant.TenantId)],
            forgotten.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(255, forgotten.FindProperty(nameof(ForgottenTenant.Projection))!.GetMaxLength());
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
        Assert.Null(write.Model.FindEntityType(typeof(ForgottenTenant)));
    }

    [Fact]
    public void Under_the_global_entity_key_the_write_model_indexes_the_sequence_number_and_the_unchained_entries()
    {
        using var context = new GlobalKeyWriteContext(SnakeCaseNpgsql<GlobalKeyWriteContext>());
        var entry = Entity<EventStreamEntry>(context.Model);

        Assert.Equal([nameof(EventStreamEntry.Id)], entry.FindPrimaryKey()!.Properties.Select(p => p.Name));
        var bySequence = Index(entry, SequenceNumberIndex);
        Assert.True(bySequence.IsUnique);
        Assert.Null(bySequence.GetFilter());
        Assert.Equal([nameof(EventStreamEntry.SequenceNumber)], bySequence.Properties.Select(p => p.Name));
        var unchained = Index(entry, UnchainedIndex);
        Assert.False(unchained.IsUnique);
        Assert.Equal("hash IS NULL", unchained.GetFilter());
        Assert.Equal([nameof(EventStreamEntry.SequenceNumber)], unchained.Properties.Select(p => p.Name));
        var anchor = Index(Entity<EventChainAnchor>(context.Model), AnchorIndex);
        Assert.False(anchor.IsUnique);
        Assert.Equal([nameof(EventChainAnchor.SequenceNumber)], anchor.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Under_the_frameworks_key_the_write_model_declares_no_second_index_over_the_sequence_number()
    {
        using var context = new SchemaWriteContext(SnakeCaseNpgsql<SchemaWriteContext>());
        var entry = Entity<EventStreamEntry>(context.Model);

        Assert.Equal([nameof(EventStreamEntry.SequenceNumber)], entry.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.DoesNotContain(entry.GetIndexes(), index =>
            index.GetFilter() is null && index.Properties.Select(p => p.Name).SequenceEqual([nameof(EventStreamEntry.SequenceNumber)]));
        Assert.Equal("hash IS NULL", Index(entry, UnchainedIndex).GetFilter());
        Index(Entity<EventChainAnchor>(context.Model), AnchorIndex);
    }

    [Fact]
    public void The_unchained_filter_names_the_column_the_model_maps_the_hash_to()
    {
        using var context = new PascalCaseWriteContext(new DbContextOptionsBuilder<PascalCaseWriteContext>()
            .UseNpgsql("Host=unused")
            .Options);
        var entry = Entity<EventStreamEntry>(context.Model);

        Assert.Equal("\"Hash\" IS NULL", Index(entry, UnchainedIndex).GetFilter());
        Index(entry, SequenceNumberIndex);
        Index(Entity<EventChainAnchor>(context.Model), AnchorIndex);
    }

    [Fact]
    public void A_context_that_replaces_the_conventions_without_base_lacks_the_event_stream_indexes()
    {
        using var context = new ConventionlessWriteContext(SnakeCaseNpgsql<ConventionlessWriteContext>());
        var entry = Entity<EventStreamEntry>(context.Model);

        Assert.DoesNotContain(entry.GetIndexes(), index => index.GetDatabaseName() is SequenceNumberIndex or UnchainedIndex);
    }

    [Fact]
    public void A_consumer_that_bridged_the_indexes_adopts_the_frameworks_without_creating_or_dropping_them()
    {
        using var bridged = new BridgedWriteContext(SnakeCaseNpgsql<BridgedWriteContext>());
        using var adopted = new GlobalKeyWriteContext(SnakeCaseNpgsql<GlobalKeyWriteContext>());

        var operations = adopted.GetService<IMigrationsModelDiffer>().GetDifferences(
            bridged.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            adopted.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.Equal(
            [BridgedWriteContext.EventsByStream, BridgedWriteContext.SnapshotByStream],
            operations.Select(operation => Assert.IsType<DropIndexOperation>(operation).Name).Order());
    }

    private const string SequenceNumberIndex = "ix_event_stream_entry_sequence_number";
    private const string UnchainedIndex = "ix_event_stream_entry_unhashed_sequence_number";
    private const string AnchorIndex = "ix_event_chain_anchor_sequence_number";

    private static IIndex Index(IEntityType entity, string name)
    {
        var index = Assert.Single(entity.GetIndexes(), index => index.GetDatabaseName() == name);
        Assert.Equal(name, index.Name);
        return index;
    }

    private static DbContextOptions<T> SnakeCaseNpgsql<T>() where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql("Host=unused")
            .UseSnakeCaseNamingConvention()
            .Options;

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

    private sealed class GlobalKeyWriteContext(DbContextOptions<GlobalKeyWriteContext> options)
        : WriteDbContext<GlobalKeyWriteContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyGlobalEntityConfiguration();
        }
    }

    private sealed class PascalCaseWriteContext(DbContextOptions<PascalCaseWriteContext> options)
        : WriteDbContext<PascalCaseWriteContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyGlobalEntityConfiguration();
        }
    }

    /// <summary>A consumer's write context that replaces the conventions without calling <c>base</c>.</summary>
    private sealed class ConventionlessWriteContext(DbContextOptions<ConventionlessWriteContext> options)
        : WriteDbContext<ConventionlessWriteContext>(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyGlobalEntityConfiguration();
        }
    }

    /// <summary>
    /// A consumer's write context before it adopts the framework's indexes: the framework declares none of its own
    /// because the context replaces the conventions without calling <c>base</c>, and the context declares them itself,
    /// under the names and definitions the framework uses, together with stream indexes of its own.
    /// </summary>
    private sealed class BridgedWriteContext(DbContextOptions<BridgedWriteContext> options)
        : WriteDbContext<BridgedWriteContext>(options)
    {
        public const string EventsByStream = "ix_event_stream_entry_stream_id_version";
        public const string SnapshotByStream = "ix_snapshot_stream_id_version";

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var entry = modelBuilder.Entity<EventStreamEntry>();
            entry.HasIndex(e => new { e.StreamId, e.Version }, EventsByStream).HasDatabaseName(EventsByStream);
            entry.HasIndex(e => e.SequenceNumber, SequenceNumberIndex).HasDatabaseName(SequenceNumberIndex).IsUnique();
            entry.HasIndex(e => e.SequenceNumber, UnchainedIndex).HasDatabaseName(UnchainedIndex).HasFilter("hash IS NULL");
            modelBuilder.Entity<Snapshot>().HasIndex(e => new { e.StreamId, e.Version }, SnapshotByStream).HasDatabaseName(SnapshotByStream);
            modelBuilder.Entity<EventChainAnchor>().HasIndex(e => e.SequenceNumber, AnchorIndex).HasDatabaseName(AnchorIndex);
            modelBuilder.ApplyGlobalEntityConfiguration();
        }
    }
}
