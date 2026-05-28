using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

public class EventChainRepositoryTests
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

    private static EventChainAnchor Anchor(int bucketId, long sequenceNumber) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.NewGuid(),
        BucketId = bucketId,
        SequenceNumber = sequenceNumber,
        AnchorHash = [(byte)bucketId, (byte)sequenceNumber],
        Timestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task GetLastSequenceNumberOrDefaultAsync_EmptyBucketIds_ReturnsGlobalMax()
    {
        await using var ctx = CreateContext();
        await ctx.Set<EventChainAnchor>().AddRangeAsync(
            Anchor(bucketId: 1, sequenceNumber: 10),
            Anchor(bucketId: 2, sequenceNumber: 99),
            Anchor(bucketId: 3, sequenceNumber: 42));
        await ctx.SaveChangesAsync();

        var repo = new EventChainRepository(ctx);

        var result = await repo.GetLastSequenceNumberOrDefaultAsync(bucketIds: [], CancellationToken.None);

        Assert.Equal(99, result);
    }

    [Fact]
    public async Task GetLastSequenceNumberOrDefaultAsync_NonEmptyBucketIds_FiltersByBucket()
    {
        await using var ctx = CreateContext();
        await ctx.Set<EventChainAnchor>().AddRangeAsync(
            Anchor(bucketId: 1, sequenceNumber: 10),
            Anchor(bucketId: 2, sequenceNumber: 99),
            Anchor(bucketId: 3, sequenceNumber: 42));
        await ctx.SaveChangesAsync();

        var repo = new EventChainRepository(ctx);

        var result = await repo.GetLastSequenceNumberOrDefaultAsync(bucketIds: [1, 3], CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task GetLastSequenceNumberOrDefaultAsync_NoMatchingAnchors_ReturnsZero()
    {
        await using var ctx = CreateContext();

        var repo = new EventChainRepository(ctx);

        var result = await repo.GetLastSequenceNumberOrDefaultAsync(bucketIds: [], CancellationToken.None);

        Assert.Equal(0L, result);
    }

    [Fact]
    public async Task AddAnchorAsync_PersistsAnchor()
    {
        await using var ctx = CreateContext();
        var repo = new EventChainRepository(ctx);
        var anchor = Anchor(bucketId: 5, sequenceNumber: 7);

        await repo.AddAnchorAsync(anchor, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var persisted = await ctx.Set<EventChainAnchor>().SingleAsync();
        Assert.Equal(5, persisted.BucketId);
        Assert.Equal(7, persisted.SequenceNumber);
    }
}
