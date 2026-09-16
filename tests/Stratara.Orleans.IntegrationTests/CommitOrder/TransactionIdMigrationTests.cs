using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// Scenario <em>A populated PostgreSQL store adopts the native reader</em>: a table populated before the commit
/// record existed, migrated in the documented three steps and backfilled in batches, reads back in batches no
/// larger than the backfill's and in append order per partition. The unedited migration — the column added non-null
/// with its volatile default — stamps the whole history with one transaction, which the reader then returns as one
/// batch per partition however small a batch was asked for. The pre-migration table is the 4.1 table with the column
/// dropped, because every 4.1 write context declares the column.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class TransactionIdMigrationTests(PostgreSqlFixture postgres)
{
    private const int Entries = 25;
    private const int BackfillBatch = 10;
    private const int ReadBatch = 3;
    private const string Table = "event_stream_entry";
    private const string Column = "commit_transaction_id";

    [Fact]
    public async Task The_documented_migration_and_the_backfill_read_back_in_bounded_batches_in_append_order()
    {
        var connectionString = postgres.ConnectionStringFor("poc_xid_migration");
        var appended = await PopulateWithoutTheColumnAsync(connectionString);

        await ExecuteAsync(connectionString, $"ALTER TABLE {Table} ADD COLUMN {Column} xid8 NULL");
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, options => options.MaintainPartitionCounter = false);
        int stamped;
        await using (var context = await store.CreateContextAsync())
        {
            stamped = await CommitTransactionIdBackfill.RunAsync(context, BackfillBatch);
            Assert.Equal(0, await CommitTransactionIdBackfill.RunAsync(context, BackfillBatch));
        }

        await ExecuteAsync(connectionString, $"ALTER TABLE {Table} ALTER COLUMN {Column} SET DEFAULT pg_current_xact_id()");
        await ExecuteAsync(connectionString, $"ALTER TABLE {Table} ALTER COLUMN {Column} SET NOT NULL");
        await ExecuteAsync(connectionString, $"CREATE INDEX IF NOT EXISTS ix_{Table}_{Column} ON {Table} ({Column})");

        Assert.Equal(Entries, stamped);
        var reader = new PostgresTransactionIdReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));
        var largestBatch = 0;
        foreach (var partition in appended.Keys)
        {
            var read = new List<long>();
            var position = 0L;
            while (true)
            {
                var batch = await reader.ReadAfterAsync(partition, position, ReadBatch);
                if (batch.Entries.Count == 0)
                {
                    break;
                }

                largestBatch = Math.Max(largestBatch, batch.Entries.Count);
                read.AddRange(batch.Entries.Select(entry => entry.Entry.SequenceNumber));
                position = batch.Position;
                if (!batch.HasMore)
                {
                    break;
                }
            }

            Assert.Equal(appended[partition], read);
        }

        Assert.True(largestBatch <= BackfillBatch, $"a batch held {largestBatch} entries, more than the backfill's batch of {BackfillBatch}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{Entries} entries stamped in batches of {BackfillBatch}; the largest batch read back held {largestBatch}");
    }

    [Fact]
    public async Task The_unedited_migration_stamps_the_history_with_one_transaction()
    {
        var connectionString = postgres.ConnectionStringFor("poc_xid_migration_unedited");
        var appended = await PopulateWithoutTheColumnAsync(connectionString);

        await ExecuteAsync(connectionString, $"ALTER TABLE {Table} ADD COLUMN {Column} xid8 NOT NULL DEFAULT pg_current_xact_id()");
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, options => options.MaintainPartitionCounter = false);
        var reader = new PostgresTransactionIdReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));

        var (partition, sequences) = appended.MaxBy(pair => pair.Value.Count);
        var batch = await reader.ReadAfterAsync(partition, 0, ReadBatch);

        Assert.Equal(sequences.Count, batch.Entries.Count);
        Assert.Single(batch.Entries.Select(entry => entry.Position).Distinct());
        TestContext.Current.TestOutputHelper?.WriteLine($"asked for {ReadBatch}, got the partition's whole history of {batch.Entries.Count} under one transaction id");
    }

    /// <summary>
    /// A table as it was before the column existed: the store is created, entries are appended over three buckets
    /// in sequence order, and the column is dropped — every write context of 4.1 declares it, so the table of an
    /// earlier version is the same table without it.
    /// </summary>
    private static async Task<Dictionary<int, List<long>>> PopulateWithoutTheColumnAsync(string connectionString)
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(connectionString);
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, options => options.MaintainPartitionCounter = false);
        await ExecuteAsync(connectionString, $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {Column} xid8 NOT NULL DEFAULT pg_current_xact_id()");
        await ExecuteAsync(connectionString, $"DELETE FROM {Table}");
        var tenantId = Guid.NewGuid();
        var buckets = new[] { 7, 8, 9 };
        var appended = new Dictionary<int, List<long>>();
        for (var i = 0; i < Entries; i++)
        {
            var bucketId = buckets[i % buckets.Length];
            await using var context = await store.CreateContextAsync();
            var entry = PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, tenantId);
            context.Set<EventStreamEntry>().Add(entry);
            await context.SaveChangesAsync();
            var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
            appended.TryAdd(partition, []);
            appended[partition].Add(entry.SequenceNumber);
        }

        await ExecuteAsync(connectionString, $"ALTER TABLE {Table} DROP COLUMN {Column}");
        return appended;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
