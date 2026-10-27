using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.IntegrationTests.EventStore;

/// <summary>
/// On a write context keyed by the framework's entity-key convention, every read of one stream or its snapshot, every
/// read the chaining worker makes and every read in sequence order is answered by an index, so its cost does not
/// grow with the store. Each read's plan is taken with <c>EXPLAIN</c> of the SQL the repository sent, with its
/// parameters, on a store large enough that the planner prefers an index wherever one serves.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class EventStoreLookupPlanTests(PostgreSqlFixture postgres)
{
    private const int Streams = 4_000;
    private const int EventsPerStream = 5;
    private const int Unchained = 3;
    private const int Anchors = 4_000;

    [Fact]
    public async Task Every_lookup_is_answered_by_an_index_under_the_global_entity_key()
    {
        var recorder = new PlanRecorder();
        var options = new DbContextOptionsBuilder<GlobalKeyPlanWriteDbContext>()
            .UseNpgsql(postgres.ConnectionStringFor("event_store_lookup_plans"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(recorder)
            .Options;
        await using var context = new GlobalKeyPlanWriteDbContext(options);
        var streamId = await SeedAsync(context, recorder);
        var events = new EventStreamRepository(context);
        var snapshots = new SnapshotRepository(context);
        var chain = new EventChainRepository(context);
        var aggregate = typeof(PlanProbe).AssemblyQualifiedName!;
        var cancellation = TestContext.Current.CancellationToken;

        await AssertStreamReadAsync(recorder, () => events.StreamExistsAsync(streamId, cancellation));
        await AssertStreamReadAsync(recorder, () => events.GetFirstOrDefaultAsync(streamId, cancellation));
        await AssertStreamReadAsync(recorder, () => events.GetManyAsync(streamId, cancellationToken: cancellation));
        await AssertStreamReadAsync(recorder, () => events.GetManyAsync(streamId, 2, 4, cancellation));
        await AssertStreamReadAsync(recorder, () => events.GetVersionOrDefaultAsync(streamId, cancellation));
        await AssertStreamReadAsync(recorder, () => snapshots.GetAsync(streamId, aggregate, cancellationToken: cancellation));
        await AssertStreamReadAsync(recorder, () => snapshots.GetLatestVersionOrDefaultAsync(streamId, aggregate, cancellation));

        var head = 0L;
        await AssertIndexedAsync(recorder, async () => head = await events.GetMaxSequenceNumberAsync(cancellation));
        await AssertIndexedAsync(recorder, () => events.GetUnhashedEventsAsync(100, DateTimeOffset.UtcNow, cancellation));
        await AssertIndexedAsync(recorder, () => events.GetPreviousEventAsync(head - 1, cancellation));
        await AssertIndexedAsync(recorder, () => events.GetLastHashedEventAsync(cancellation));
        await AssertIndexedAsync(recorder, () => events.GetManyAfterSequenceAsync(head - 10, 100, cancellation));
        await AssertIndexedAsync(recorder, () => chain.GetLastSequenceNumberOrDefaultAsync([], cancellation));
    }

    [Fact]
    public async Task The_unchained_entries_are_found_by_index_where_the_hash_column_is_not_snake_case()
    {
        var recorder = new PlanRecorder();
        var options = new DbContextOptionsBuilder<PascalCasePlanWriteDbContext>()
            .UseNpgsql(postgres.ConnectionStringFor("event_store_lookup_plans_pascal"))
            .AddInterceptors(recorder)
            .Options;
        await using var context = new PascalCasePlanWriteDbContext(options);
        await SeedAsync(context, recorder);
        var events = new EventStreamRepository(context);

        await AssertIndexedAsync(recorder, () => events.GetUnhashedEventsAsync(100, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        await AssertIndexedAsync(recorder, () => events.GetLastHashedEventAsync(TestContext.Current.CancellationToken));
    }

    private static async Task AssertStreamReadAsync(PlanRecorder recorder, Func<Task> read)
    {
        var plan = await PlanOfAsync(recorder, read);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
        Assert.Matches(@"Index Cond: \(\(bucket_id = .*\) AND \(stream_id = ", plan);
    }

    private static async Task AssertIndexedAsync(PlanRecorder recorder, Func<Task> read)
    {
        var plan = await PlanOfAsync(recorder, read);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
        Assert.Contains("Index", plan, StringComparison.Ordinal);
    }

    private static async Task<string> PlanOfAsync(PlanRecorder recorder, Func<Task> read)
    {
        recorder.Plans.Clear();
        await read();
        var plan = Assert.Single(recorder.Plans);
        recorder.Plans.Clear();
        return plan;
    }

    /// <summary>
    /// Creates the schema and fills it: <see cref="Streams"/> streams of <see cref="EventsPerStream"/> events each, all
    /// chained but the last <see cref="Unchained"/>, a snapshot per stream and <see cref="Anchors"/> anchors. Returns
    /// a stream in the middle of the store.
    /// </summary>
    private static async Task<Guid> SeedAsync(DbContext context, PlanRecorder recorder)
    {
        recorder.Enabled = false;
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var tenantId = Guid.NewGuid();
        var streamIds = Enumerable.Range(0, Streams).Select(_ => Guid.NewGuid()).ToList();
        var hash = new byte[32];
        var aggregate = typeof(PlanProbe).AssemblyQualifiedName!;
        for (var version = 1; version <= EventsPerStream; version++)
        {
            foreach (var streamId in streamIds)
            {
                context.Add(new EventStreamEntry
                {
                    Id = Guid.CreateVersion7(),
                    StreamId = streamId,
                    Version = version,
                    EventTypeName = "Stratara.Orleans.IntegrationTests.PlanProbeHappened",
                    AggregateTypeName = aggregate,
                    DataJson = "{}",
                    Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
                    CorrelationId = "plan",
                    CausationId = "plan",
                    Hash = hash,
                    BucketId = BucketCalculator.GetBucketId(streamId),
                    TenantId = tenantId,
                    ActorTenantId = tenantId,
                    ActorUserId = tenantId,
                });
            }

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        foreach (var streamId in streamIds)
        {
            context.Add(new Snapshot
            {
                Id = Guid.CreateVersion7(),
                StreamId = streamId,
                Version = EventsPerStream,
                AggregateTypeName = aggregate,
                DataJson = "{}",
                Timestamp = DateTimeOffset.UtcNow,
                BucketId = BucketCalculator.GetBucketId(streamId),
                TenantId = tenantId,
            });
        }

        for (var anchor = 1; anchor <= Anchors; anchor++)
        {
            context.Add(new EventChainAnchor
            {
                Id = Guid.CreateVersion7(),
                SequenceNumber = anchor * 5L,
                AnchorHash = hash,
                Timestamp = DateTimeOffset.UtcNow,
                BucketId = anchor % BucketConstants.TotalBucketCount,
                TenantId = tenantId,
            });
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var latest = await context.Set<EventStreamEntry>().OrderByDescending(e => e.SequenceNumber).Take(Unchained).ToListAsync();
        foreach (var entry in latest)
        {
            entry.Hash = null;
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await context.Database.ExecuteSqlRawAsync("ANALYZE");
        recorder.Enabled = true;
        return streamIds[Streams / 2];
    }

    /// <summary>Takes the plan of every query a repository sends, with its parameters, before the query runs.</summary>
    private sealed class PlanRecorder : DbCommandInterceptor
    {
        public bool Enabled { get; set; } = true;

        public List<string> Plans { get; } = [];

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await ExplainAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            await ExplainAsync(command, cancellationToken);
            return result;
        }

        private async Task ExplainAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!Enabled || !command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await using var explain = command.Connection!.CreateCommand();
            explain.Transaction = command.Transaction;
            explain.CommandText = "EXPLAIN " + command.CommandText;
            foreach (DbParameter parameter in command.Parameters)
            {
                var copy = explain.CreateParameter();
                copy.ParameterName = parameter.ParameterName;
                copy.DbType = parameter.DbType;
                copy.Value = parameter.Value;
                explain.Parameters.Add(copy);
            }

            var lines = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    lines.Add(reader.GetString(0));
                }
            }

            Plans.Add(string.Join('\n', lines));
        }
    }

    private sealed class PlanProbe;
}

/// <summary>The shipped write model keyed by the framework's entity-key convention, as both consumers key it.</summary>
public sealed class GlobalKeyPlanWriteDbContext(DbContextOptions<GlobalKeyPlanWriteDbContext> options)
    : WriteDbContext<GlobalKeyPlanWriteDbContext>(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyGlobalEntityConfiguration();
    }
}

/// <summary>The same model under the default PascalCase names, so the chain hash is the column <c>"Hash"</c>.</summary>
public sealed class PascalCasePlanWriteDbContext(DbContextOptions<PascalCasePlanWriteDbContext> options)
    : WriteDbContext<PascalCasePlanWriteDbContext>(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyGlobalEntityConfiguration();
    }
}
