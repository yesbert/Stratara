using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

/// <summary>
/// A save does not number its entries in version order: these tests write rows with explicit sequence numbers so
/// that a stream's versions sit at sequence numbers running the other way, as they do in a store whose save order
/// EF Core chose.
/// </summary>
public class EventStreamRepositoryStreamOrderTests
{
    private static TestWriteDbContext CreateContext()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TestWriteDbContext>()
            .UseSqlite(connection)
            .Options;
        var ctx = new TestWriteDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private static EventStreamEntry Entry(long sequenceNumber, Guid streamId, long version) => new()
    {
        StreamId = streamId,
        Version = version,
        EventTypeName = "TestEvent",
        AggregateTypeName = "TestAggregate",
        DataJson = "{}",
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = Guid.NewGuid().ToString("N"),
        CausationId = Guid.NewGuid().ToString("N"),
        BucketId = 3,
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ActorTenantId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        SequenceNumber = sequenceNumber
    };

    private static async Task<List<EventStreamEntry>> DrainAsync(EventStreamRepository repo, int batchSize)
    {
        var read = new List<EventStreamEntry>();
        var after = 0L;
        while (true)
        {
            var batch = await repo.GetManyAfterSequenceInStreamOrderAsync(after, batchSize, TestContext.Current.CancellationToken);
            if (batch.Count == 0)
            {
                return read;
            }

            read.AddRange(batch);
            after = batch.Max(e => e.SequenceNumber);
        }
    }

    [Fact]
    public async Task A_stream_numbered_against_its_versions_is_returned_in_version_order()
    {
        await using var ctx = CreateContext();
        var stream = Guid.NewGuid();
        await ctx.Set<EventStreamEntry>().AddRangeAsync(Entry(20, stream, 3), Entry(21, stream, 2), Entry(22, stream, 1));
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repo = new EventStreamRepository(ctx);

        var batch = await repo.GetManyAfterSequenceInStreamOrderAsync(0, 10, TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L], batch.Select(e => e.Version));
    }

    [Fact]
    public async Task A_batch_that_would_end_inside_the_run_is_extended_and_nothing_is_read_twice()
    {
        await using var ctx = CreateContext();
        var stream = Guid.NewGuid();
        var other = Guid.NewGuid();
        await ctx.Set<EventStreamEntry>().AddRangeAsync(
            Entry(10, other, 1), Entry(20, stream, 3), Entry(21, stream, 2), Entry(22, stream, 1), Entry(30, other, 2));
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repo = new EventStreamRepository(ctx);

        var first = await repo.GetManyAfterSequenceInStreamOrderAsync(0, 2, TestContext.Current.CancellationToken);
        var all = await DrainAsync(repo, 2);

        Assert.Equal(4, first.Count);
        Assert.Equal(22, first.Max(e => e.SequenceNumber));
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Select(e => e.Id).Distinct().Count());
        Assert.Equal([1L, 2L, 3L], all.Where(e => e.StreamId == stream).Select(e => e.Version));
        Assert.Equal([1L, 2L], all.Where(e => e.StreamId == other).Select(e => e.Version));
    }

    [Fact]
    public async Task Two_inverted_streams_keep_the_interleaving_the_sequence_gives_them()
    {
        await using var ctx = CreateContext();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await ctx.Set<EventStreamEntry>().AddRangeAsync(
            Entry(1, a, 2), Entry(2, b, 3), Entry(3, a, 1), Entry(4, b, 2), Entry(5, b, 1));
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repo = new EventStreamRepository(ctx);

        var batch = await repo.GetManyAfterSequenceInStreamOrderAsync(0, 10, TestContext.Current.CancellationToken);

        Assert.Equal([a, b, a, b, b], batch.Select(e => e.StreamId));
        Assert.Equal([1L, 1L, 2L, 2L, 3L], batch.Select(e => e.Version));
    }

    [Fact]
    public async Task A_store_without_inversions_is_returned_exactly_as_in_sequence_order()
    {
        await using var ctx = CreateContext();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await ctx.Set<EventStreamEntry>().AddRangeAsync(
            Entry(1, a, 1), Entry(2, b, 1), Entry(3, a, 2), Entry(4, b, 2), Entry(5, a, 3), Entry(6, b, 3));
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repo = new EventStreamRepository(ctx);

        foreach (var after in new long[] { 0, 2, 4 })
        {
            var ordered = await repo.GetManyAfterSequenceInStreamOrderAsync(after, 2, TestContext.Current.CancellationToken);
            var sequential = await repo.GetManyAfterSequenceAsync(after, 2, TestContext.Current.CancellationToken);

            Assert.Equal(sequential.Select(e => e.Id), ordered.Select(e => e.Id));
        }
    }

    [Fact]
    public async Task An_empty_range_returns_nothing()
    {
        await using var ctx = CreateContext();
        var repo = new EventStreamRepository(ctx);

        Assert.Empty(await repo.GetManyAfterSequenceInStreamOrderAsync(0, 10, TestContext.Current.CancellationToken));
    }
}
