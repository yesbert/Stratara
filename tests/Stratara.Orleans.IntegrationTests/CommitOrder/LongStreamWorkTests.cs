using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// Scenarios <em>One stream holds a large share of a long history</em> and <em>One stream holds a large share of the
/// history a backfill prepares</em>, on PostgreSQL. Half the history belongs to one stream, spread through all of it,
/// and every forty-ninth save of that stream numbered its two entries against their versions, so that some of those
/// pairs straddle a batch boundary. The replay's read and both
/// backfills walk it in batches far smaller than the stream: each returns or prepares every stream in version order,
/// and the rows it reads from the event table — index entries and heap rows alike, counted by the server — stay
/// within a small multiple of the rows the table holds. A search for a batch's lower versions that re-read the long
/// stream's past in every batch reads many times more. The backfills run with the column they fill indexed and their
/// statistics refreshed as they go, as autovacuum refreshes them during a long run: a batch that asked that index for
/// the entries still to prepare would read all of them — and the ones already prepared, until the table is vacuumed —
/// once the statistics show part of the store prepared. The native backfill's index is created before it runs,
/// earlier than the migration guide creates it, because that is the harder case.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class LongStreamWorkTests(PostgreSqlFixture postgres)
{
    private const int Entries = 100_000;
    private const int ReadBatch = 100;
    private const int StampBatch = 100;
    private const int RowsReadPerEntry = 16;
    private const string Table = "event_stream_entry";
    private static readonly Guid LongStream = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task The_replays_read_returns_every_stream_in_order_and_reads_the_long_stream_once()
    {
        await using var store = await PopulateAsync("poc_long_stream_replay");
        await using var context = await store.CreateContextAsync();
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var connection = context.Database.GetDbConnection();
        var repository = new EventStreamRepository(context);

        var before = await RowsReadAsync(connection);
        var versions = new Dictionary<Guid, long>();
        var read = 0;
        var after = 0L;
        while (true)
        {
            var batch = await repository.GetManyAfterSequenceInStreamOrderAsync(after, ReadBatch, TestContext.Current.CancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var entry in batch)
            {
                Assert.Equal(versions.GetValueOrDefault(entry.StreamId) + 1, entry.Version);
                versions[entry.StreamId] = entry.Version;
            }

            read += batch.Count;
            after = batch.Max(entry => entry.SequenceNumber);
        }

        var rowsRead = await RowsReadAsync(connection) - before;

        Assert.Equal(Entries, read);
        Assert.InRange(rowsRead, Entries, (long)Entries * RowsReadPerEntry);
    }

    [Fact]
    public async Task The_portable_backfill_positions_every_stream_in_order_and_reads_the_long_stream_once()
    {
        await using var store = await PopulateAsync("poc_long_stream_portable", partitionCount: 1);
        await using var context = await store.CreateContextAsync();
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var connection = context.Database.GetDbConnection();

        var before = await RowsReadAsync(connection);
        int positioned;
        await using (new StatisticsRefresh(postgres.ConnectionStringFor("poc_long_stream_portable")))
        {
            positioned = await PartitionCounterBackfill.RunAsync(context, store.Options, TestContext.Current.CancellationToken);
        }

        var rowsRead = await RowsReadAsync(connection) - before;

        Assert.Equal(Entries, positioned);
        Assert.Equal(0, await CountAsync(connection, $"""
            SELECT count(*) FROM (
                SELECT partition_position, lag(partition_position) OVER (PARTITION BY bucket_id, stream_id ORDER BY version) AS previous
                FROM {Table}
            ) AS ordered WHERE partition_position IS NULL OR partition_position < previous
            """));
        Assert.InRange(rowsRead, Entries, (long)Entries * RowsReadPerEntry);
    }

    [Fact]
    public async Task The_native_backfill_stamps_every_stream_in_order_and_reads_the_long_stream_once()
    {
        await using var store = await PopulateAsync("poc_long_stream_native");
        await using var context = await store.CreateContextAsync();
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var connection = context.Database.GetDbConnection();
        await CountAsync(connection, $"ALTER TABLE {Table} DROP COLUMN commit_transaction_id");
        await CountAsync(connection, $"ALTER TABLE {Table} ADD COLUMN commit_transaction_id xid8 NULL");
        await CountAsync(connection, $"CREATE INDEX ix_{Table}_commit_transaction_id ON {Table} (commit_transaction_id)");

        var before = await RowsReadAsync(connection);
        int stamped;
        await using (new StatisticsRefresh(postgres.ConnectionStringFor("poc_long_stream_native")))
        {
            stamped = await CommitTransactionIdBackfill.RunAsync(context, StampBatch, TestContext.Current.CancellationToken);
        }

        var rowsRead = await RowsReadAsync(connection) - before;

        Assert.Equal(Entries, stamped);
        Assert.Equal(0, await CountAsync(connection, $"""
            SELECT count(*) FROM (
                SELECT commit_transaction_id, lag(commit_transaction_id) OVER (PARTITION BY bucket_id, stream_id ORDER BY version) AS previous
                FROM {Table}
            ) AS ordered WHERE commit_transaction_id IS NULL OR commit_transaction_id < previous
            """));
        Assert.InRange(rowsRead, Entries, (long)Entries * RowsReadPerEntry);
    }

    /// <summary>
    /// A store of <see cref="Entries"/> entries: every other one belongs to <see cref="LongStream"/>, the rest to streams
    /// of three. Every forty-ninth pair of the long stream's entries carries its two versions the other way round, as a
    /// save that numbered them against their order leaves them; the odd spacing lets some pairs straddle a batch boundary.
    /// </summary>
    private async Task<PocStore<PocCommitOrderWriteDbContext>> PopulateAsync(string database, int partitionCount = 16)
    {
        var connectionString = postgres.ConnectionStringFor(database);
        var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            connectionString, options => options.PartitionCount = partitionCount, maintainCounter: false);
        await using var context = await store.CreateContextAsync();
        await context.Set<EventStreamEntry>().ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        context.Set<EventStreamEntry>().Add(PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, 0, Guid.NewGuid()));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var populate = $"""
            CREATE TEMP TABLE template AS SELECT * FROM {Table} LIMIT 1;
            DELETE FROM {Table};
            INSERT INTO {Table} (stream_id, version, bucket_id, id, event_type_name, aggregate_type_name, data_json,
                "timestamp", correlation_id, causation_id, tenant_id, user_id, actor_tenant_id, actor_user_id, row_version)
            SELECT g.stream_id, g.version, g.bucket_id, gen_random_uuid(), t.event_type_name, t.aggregate_type_name, t.data_json,
                t."timestamp", t.correlation_id, t.causation_id, t.tenant_id, t.user_id, t.actor_tenant_id, t.actor_user_id, t.row_version
            FROM (
                SELECT i,
                    CASE WHEN i % 2 = 0 THEN '{LongStream}'::uuid ELSE md5(((i / 2) / 3)::text)::uuid END AS stream_id,
                    CASE WHEN i % 2 = 1 THEN (i / 2) % 3 + 1
                         WHEN (i / 2) % 49 = 0 THEN i / 2 + 2
                         WHEN (i / 2) % 49 = 1 THEN i / 2
                         ELSE i / 2 + 1 END AS version,
                    CASE WHEN i % 2 = 0 THEN 0 ELSE ((i / 2) / 3) % 64 END AS bucket_id
                FROM generate_series(0, {Entries - 1}) AS i
            ) AS g CROSS JOIN template AS t
            ORDER BY g.i;
            DROP TABLE template;
            ANALYZE {Table};
            """;
        await context.Database.ExecuteSqlRawAsync(populate, TestContext.Current.CancellationToken);
        return store;
    }

    /// <summary>
    /// The rows this connection's backend has read from the event table so far — heap rows read by sequential scans
    /// and index entries read by index scans — once its pending statistics are flushed, which the server does before it
    /// answers the statement that asks for it.
    /// </summary>
    private static async Task<long> RowsReadAsync(DbConnection connection)
    {
        await CountAsync(connection, "SELECT pg_stat_force_next_flush()");
        return await CountAsync(connection, $"""
            SELECT pg_stat_get_tuples_returned(t.oid) + coalesce(sum(pg_stat_get_tuples_returned(i.indexrelid)), 0)
            FROM pg_class AS t LEFT JOIN pg_index AS i ON i.indrelid = t.oid
            WHERE t.oid = '{Table}'::regclass
            GROUP BY t.oid
            """);
    }

    /// <summary>Analyzes the event table over a connection of its own, again and again, until disposed.</summary>
    private sealed class StatisticsRefresh : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _refreshing;

        public StatisticsRefresh(string connectionString) => _refreshing = RefreshAsync(connectionString, _stop.Token);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _refreshing;
            _stop.Dispose();
        }

        private static async Task RefreshAsync(string connectionString, CancellationToken stop)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(CancellationToken.None);
            while (!stop.IsCancellationRequested)
            {
                await CountAsync(connection, $"ANALYZE {Table}");
                await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            }
        }
    }

    private static async Task<long> CountAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
