using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.Outbox;
using Stratara.Shared.Reflections;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Repositories;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

public class OutboxRepositoryTests
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

    private static TestWriteDbContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestWriteDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var ctx = new TestWriteDbContext(options);
        return ctx;
    }

    [Fact]
    public async Task AddAsync_Persists_OutboxEntry_With_Correct_Fields()
    {
        await using var ctx = CreateContext(nameof(AddAsync_Persists_OutboxEntry_With_Correct_Fields));
        var repo = new OutboxRepository(ctx);

        var payload = new DummyPayload("alpha", 42);
        await repo.AddAsync(payload, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var entry = await ctx.Set<OutboxEntry>().SingleAsync();
        Assert.Equal(typeof(DummyPayload).GetQualifiedTypeName(), entry.DataTypeName);
        Assert.Equal(JsonSerializer.Serialize(payload), entry.DataJson);
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.True(entry.BucketId >= 0);
        Assert.True(entry.Timestamp <= DateTime.UtcNow);
    }

    [Fact]
    public async Task GetManyAsync_Filters_By_Type_Orders_By_Timestamp_And_Takes_Batch()
    {
        await using var ctx = CreateInMemoryContext(nameof(GetManyAsync_Filters_By_Type_Orders_By_Timestamp_And_Takes_Batch));

        // Seed entries for two types and various timestamps
        var now = DateTime.UtcNow;
        var t1 = typeof(DummyPayload).GetQualifiedTypeName();
        var t2 = typeof(string).GetQualifiedTypeName();

        OutboxEntry Make(Guid id, string type, int tsOffset) => new()
        {
            Id = id,
            BucketId = 0,
            DataJson = "{}",
            DataTypeName = type,
            Timestamp = now.AddMinutes(tsOffset)
        };

        var ids = new[]
        {
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()
        };

        await ctx.Set<OutboxEntry>().AddRangeAsync(Make(ids[0], t1, 10), Make(ids[1], t1, -10), Make(ids[2], t1, 0), Make(ids[3], t2, -100));
        await ctx.SaveChangesAsync();

        var repo = new OutboxRepository(ctx);
        var result = await repo.GetManyAsync<DummyPayload>(2, CancellationToken.None);

        Assert.Equal(2, result.Count);
        // Should be ordered by Timestamp ascending for DummyPayload only
        Assert.True(result[0].Timestamp <= result[1].Timestamp);
        Assert.All(result, r => Assert.Equal(t1, r.DataTypeName));
        Assert.Equal(ids[1], result[0].Id); // -10 first
        Assert.Equal(ids[2], result[1].Id); // 0 second
    }

    [Fact]
    public async Task DeleteAsync_Removes_Entry()
    {
        await using var ctx = CreateContext(nameof(DeleteAsync_Removes_Entry));
        var id = Guid.CreateVersion7();
        await ctx.Set<OutboxEntry>().AddAsync(new OutboxEntry
        {
            Id = id,
            BucketId = 0,
            DataJson = "{}",
            DataTypeName = typeof(DummyPayload).GetQualifiedTypeName(),
            Timestamp = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var repo = new OutboxRepository(ctx);
        await repo.DeleteAsync(id, CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.Empty(ctx.Set<OutboxEntry>());
    }

    private sealed record DummyPayload(string Name, int Value);
}