using Microsoft.Data.Sqlite;
using Stratara.Abstractions.EventSourcing;
using Microsoft.EntityFrameworkCore;
using Stratara.Shared.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

public class EventStreamRepositorySequentialTests
{
    private static TestWriteDbContext CreateContext(string dbName)
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

    private static EventStreamEntry CreateEntry(long sequenceNumber, string eventTypeName = "TestEvent") => new()
    {
        StreamId = Guid.NewGuid(),
        Version = 1,
        EventTypeName = eventTypeName,
        AggregateTypeName = "TestAggregate",
        DataJson = "{}",
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = Guid.NewGuid().ToString("N"),
        CausationId = Guid.NewGuid().ToString("N"),
        BucketId = 0,
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ActorTenantId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        SequenceNumber = sequenceNumber
    };

    [Fact]
    public async Task GetManyAfterSequenceAsync_ReturnsEmpty_WhenNoEventsExist()
    {
        await using var ctx = CreateContext(nameof(GetManyAfterSequenceAsync_ReturnsEmpty_WhenNoEventsExist));
        var repo = new EventStreamRepository(ctx);

        var result = await repo.GetManyAfterSequenceAsync(0, 100);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetManyAfterSequenceAsync_ReturnsEventsAfterSequenceNumber()
    {
        await using var ctx = CreateContext(nameof(GetManyAfterSequenceAsync_ReturnsEventsAfterSequenceNumber));

        await ctx.Set<EventStreamEntry>().AddRangeAsync(
            CreateEntry(1), CreateEntry(2), CreateEntry(3), CreateEntry(4), CreateEntry(5));
        await ctx.SaveChangesAsync();

        var repo = new EventStreamRepository(ctx);
        var result = await repo.GetManyAfterSequenceAsync(3, 100);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e.SequenceNumber > 3));
    }

    [Fact]
    public async Task GetManyAfterSequenceAsync_RespectsPageSize()
    {
        await using var ctx = CreateContext(nameof(GetManyAfterSequenceAsync_RespectsPageSize));

        await ctx.Set<EventStreamEntry>().AddRangeAsync(
            CreateEntry(1), CreateEntry(2), CreateEntry(3), CreateEntry(4), CreateEntry(5));
        await ctx.SaveChangesAsync();

        var repo = new EventStreamRepository(ctx);
        var result = await repo.GetManyAfterSequenceAsync(0, 3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetManyAfterSequenceAsync_ReturnsInSequenceOrder()
    {
        await using var ctx = CreateContext(nameof(GetManyAfterSequenceAsync_ReturnsInSequenceOrder));

        await ctx.Set<EventStreamEntry>().AddRangeAsync(
            CreateEntry(5), CreateEntry(1), CreateEntry(3));
        await ctx.SaveChangesAsync();

        var repo = new EventStreamRepository(ctx);
        var result = await repo.GetManyAfterSequenceAsync(0, 100);

        Assert.Equal(3, result.Count);
        Assert.True(result[0].SequenceNumber < result[1].SequenceNumber);
        Assert.True(result[1].SequenceNumber < result[2].SequenceNumber);
    }

    [Fact]
    public async Task GetMaxSequenceNumberAsync_ReturnsZero_WhenNoEventsExist()
    {
        await using var ctx = CreateContext(nameof(GetMaxSequenceNumberAsync_ReturnsZero_WhenNoEventsExist));
        var repo = new EventStreamRepository(ctx);

        var result = await repo.GetMaxSequenceNumberAsync();

        Assert.Equal(0L, result);
    }

    [Fact]
    public async Task GetMaxSequenceNumberAsync_ReturnsHighestSequenceNumber()
    {
        await using var ctx = CreateContext(nameof(GetMaxSequenceNumberAsync_ReturnsHighestSequenceNumber));

        await ctx.Set<EventStreamEntry>().AddRangeAsync(
            CreateEntry(3), CreateEntry(7), CreateEntry(5));
        await ctx.SaveChangesAsync();

        var repo = new EventStreamRepository(ctx);
        var result = await repo.GetMaxSequenceNumberAsync();

        Assert.Equal(7L, result);
    }
}
