using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.IntegrationTests.EventStore;

/// <summary>
/// The check and the repair the composition guide gives for a store written before 3.0.0, whose releases worked out a
/// stream's partition as <c>Math.Abs(hash) % 4096</c>. The function is <see cref="BucketCalculator.GetBucketId"/> in
/// SQL, the check counts the rows outside their stream's partition, and the repair moves them there, after which every
/// read of the stream sees them again. The statements are the guide's, word for word, which the first test pins.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class StreamPartitionRepairTests(PostgreSqlFixture postgres)
{
    private const string BucketFunction = """
        CREATE FUNCTION pg_temp.stratara_bucket(id uuid) RETURNS integer LANGUAGE sql IMMUTABLE AS $$
            SELECT ((get_byte(b, 3) | ((get_byte(b, 2) & 15) << 8))
                  # (get_byte(b, 5) | ((get_byte(b, 4) & 15) << 8))
                  # (get_byte(b, 8) | ((get_byte(b, 9) & 15) << 8))
                  # (get_byte(b, 12) | ((get_byte(b, 13) & 15) << 8)))
            FROM (SELECT uuid_send(id) AS b) AS bytes
        $$;
        """;

    private const string Check = """
        SELECT 'event_stream_entry' AS rows_in, count(*) FROM event_stream_entry
            WHERE bucket_id <> pg_temp.stratara_bucket(stream_id)
        UNION ALL
        SELECT 'snapshot', count(*) FROM snapshot
            WHERE bucket_id <> pg_temp.stratara_bucket(stream_id);
        """;

    private const string Repair = """
        BEGIN;
        UPDATE event_stream_entry SET bucket_id = pg_temp.stratara_bucket(stream_id)
            WHERE bucket_id <> pg_temp.stratara_bucket(stream_id);
        UPDATE snapshot SET bucket_id = pg_temp.stratara_bucket(stream_id)
            WHERE bucket_id <> pg_temp.stratara_bucket(stream_id);
        COMMIT;
        """;

    [Fact]
    public void The_guide_gives_these_statements()
    {
        var guide = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "getting-started", "di-composition.md"));

        Assert.Contains(BucketFunction.Trim(), guide, StringComparison.Ordinal);
        Assert.Contains(Check.Trim(), guide, StringComparison.Ordinal);
        Assert.Contains(Repair.Trim(), guide, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_function_gives_the_partition_the_framework_gives()
    {
        var ids = Enumerable.Range(0, 20_000).Select(_ => Guid.NewGuid())
            .Append(Guid.Empty)
            .Append(Guid.AllBitsSet)
            .Append(Guid.Parse("80000000-0000-0000-0000-000000000000"))
            .ToArray();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(connection, BucketFunction);

        await using var query = new NpgsqlCommand("SELECT id, pg_temp.stratara_bucket(id) FROM unnest(@ids) AS id", connection);
        query.Parameters.AddWithValue("ids", ids);
        var buckets = new Dictionary<Guid, int>();
        await using (var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                buckets[reader.GetGuid(0)] = reader.GetInt32(1);
            }
        }

        Assert.Equal(ids.Length, buckets.Count);
        Assert.All(ids, id => Assert.Equal(BucketCalculator.GetBucketId(id), buckets[id]));
    }

    [Fact]
    public async Task The_check_finds_and_the_repair_moves_the_rows_an_early_release_wrote()
    {
        var database = "stream_partition_repair";
        var options = new DbContextOptionsBuilder<GlobalKeyPlanWriteDbContext>()
            .UseNpgsql(postgres.ConnectionStringFor(database))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new GlobalKeyPlanWriteDbContext(options);
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var split = StreamWhoseEarlyPartitionDiffers();
        var early = StreamWhoseEarlyPartitionDiffers();
        context.AddRange(
            Entry(split, 1, EarlyBucket(split)),
            Entry(split, 2, EarlyBucket(split)),
            Entry(split, 3, BucketCalculator.GetBucketId(split)),
            Entry(early, 1, EarlyBucket(early)));
        context.Add(new Snapshot
        {
            Id = Guid.CreateVersion7(),
            StreamId = split,
            Version = 2,
            AggregateTypeName = "Sample.Aggregate, Sample",
            DataJson = "{}",
            Timestamp = DateTimeOffset.UtcNow,
            BucketId = EarlyBucket(split),
            TenantId = Guid.Empty,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var events = new EventStreamRepository(context);
        var snapshots = new SnapshotRepository(context);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal([3L], (await events.GetManyAsync(split, cancellationToken: ct)).Select(e => e.Version));
        Assert.False(await events.StreamExistsAsync(early, ct));
        Assert.Null(await snapshots.GetAsync(split, "Sample.Aggregate, Sample", cancellationToken: ct));

        await using var connection = new NpgsqlConnection(postgres.ConnectionStringFor(database));
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, BucketFunction);
        Assert.Equal(new Dictionary<string, long> { ["event_stream_entry"] = 3, ["snapshot"] = 1 }, await CheckAsync(connection));

        await ExecuteAsync(connection, Repair);

        Assert.Equal(new Dictionary<string, long> { ["event_stream_entry"] = 0, ["snapshot"] = 0 }, await CheckAsync(connection));
        Assert.Equal([1L, 2L, 3L], (await events.GetManyAsync(split, cancellationToken: ct)).Select(e => e.Version));
        Assert.True(await events.StreamExistsAsync(early, ct));
        Assert.Equal(2L, (await snapshots.GetAsync(split, "Sample.Aggregate, Sample", cancellationToken: ct))?.Version);
    }

    /// <summary>The partition the releases before 3.0.0 gave a stream id.</summary>
    private static int EarlyBucket(Guid id) => Math.Abs(id.GetHashCode()) % BucketConstants.TotalBucketCount;

    private static Guid StreamWhoseEarlyPartitionDiffers()
    {
        while (true)
        {
            var id = Guid.NewGuid();
            if (id.GetHashCode() != int.MinValue && EarlyBucket(id) != BucketCalculator.GetBucketId(id))
            {
                return id;
            }
        }
    }

    private static EventStreamEntry Entry(Guid streamId, long version, int bucketId) => new()
    {
        Id = Guid.CreateVersion7(),
        StreamId = streamId,
        Version = version,
        EventTypeName = "Sample.Happened",
        AggregateTypeName = "Sample.Aggregate, Sample",
        DataJson = "{}",
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = "repair",
        CausationId = "repair",
        BucketId = bucketId,
        TenantId = Guid.Empty,
        ActorTenantId = Guid.Empty,
        ActorUserId = Guid.Empty,
    };

    private static async Task<Dictionary<string, long>> CheckAsync(NpgsqlConnection connection)
    {
        await using var check = new NpgsqlCommand(Check, connection);
        var counts = new Dictionary<string, long>();
        await using var reader = await check.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(directory.FullName, "docs", "getting-started")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test's output directory.");
    }
}
