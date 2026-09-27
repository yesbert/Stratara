using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

/// <summary>
/// Scenarios <em>The write context declares the tenant filter</em> and <em>A stream is read through a filtered write
/// context</em>, for the reads a replay and the hash chain make. The write context filters every tenant-scoped entity
/// to its ambient tenant, as the tenant-isolation guide switches the filter on, and the store holds the entries and
/// anchors of two tenants. The reads across the store see both tenants whatever the ambient tenant; a read about one
/// stream sees only the ambient tenant's.
/// </summary>
public class TenantFilteredStoreReadTests
{
    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();
    private static readonly Guid FirstStream = Guid.NewGuid();
    private static readonly Guid SecondStream = Guid.NewGuid();

    [Fact]
    public async Task The_reads_across_the_store_see_every_tenant_without_a_session()
    {
        await using var context = await CreateStoreAsync();
        var entries = new EventStreamRepository(context);
        var anchors = new EventChainRepository(context);
        var ct = TestContext.Current.CancellationToken;

        Assert.Empty(await context.Set<EventStreamEntry>().ToListAsync(ct));
        Assert.Equal([1L, 2L, 3L, 4L], (await entries.GetManyAfterSequenceAsync(0, 10, ct)).Select(e => e.SequenceNumber));
        Assert.Equal([1L, 2L], (await entries.GetManyAfterSequenceInStreamOrderAsync(0, 2, ct))
            .Where(e => e.StreamId == SecondStream).Select(e => e.Version));
        Assert.Equal(4L, await entries.GetMaxSequenceNumberAsync(ct));
        Assert.Equal([3L, 4L], (await entries.GetUnhashedEventsAsync(10, DateTimeOffset.UtcNow.AddMinutes(1), ct)).Select(e => e.SequenceNumber));
        Assert.Equal(2L, (await entries.GetPreviousEventAsync(3, ct))?.SequenceNumber);
        Assert.Equal(2L, (await entries.GetLastHashedEventAsync(ct))?.SequenceNumber);
        Assert.Equal(2L, await anchors.GetLastSequenceNumberOrDefaultAsync([], ct));
    }

    [Fact]
    public async Task A_read_about_one_stream_keeps_the_filter()
    {
        await using var context = await CreateStoreAsync();
        context.TenantId = First;
        var entries = new EventStreamRepository(context);
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await entries.StreamExistsAsync(FirstStream, ct));
        Assert.False(await entries.StreamExistsAsync(SecondStream, ct));
        Assert.Null(await entries.GetFirstOrDefaultAsync(SecondStream, ct));
        Assert.Equal(0L, await entries.GetVersionOrDefaultAsync(SecondStream, ct));
        Assert.Empty(await entries.GetManyAsync(SecondStream, cancellationToken: ct));
        Assert.Equal(2L, (await entries.GetManyAfterSequenceAsync(0, 10, ct)).Count(e => e.TenantId == Second));
    }

    /// <summary>
    /// The first tenant's stream holds the first and the third entry; the second tenant's stream holds the second and
    /// the fourth, numbered against its versions. The first two entries are hashed, and each tenant has an anchor.
    /// </summary>
    private static async Task<TenantFilteredWriteDbContext> CreateStoreAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<TenantFilteredWriteDbContext>().UseSqlite(connection).Options;
        var context = new TenantFilteredWriteDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        context.Set<EventStreamEntry>().AddRange(
            Entry(1, First, FirstStream, 1, hashed: true),
            Entry(2, Second, SecondStream, 2, hashed: true),
            Entry(3, First, FirstStream, 2, hashed: false),
            Entry(4, Second, SecondStream, 1, hashed: false));
        context.Set<EventChainAnchor>().AddRange(Anchor(First, 1), Anchor(Second, 2));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        return context;
    }

    private static EventStreamEntry Entry(long sequenceNumber, Guid tenantId, Guid streamId, long version, bool hashed) => new()
    {
        SequenceNumber = sequenceNumber,
        StreamId = streamId,
        Version = version,
        EventTypeName = "TestEvent",
        AggregateTypeName = "TestAggregate",
        DataJson = "{}",
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
        CorrelationId = Guid.NewGuid().ToString("N"),
        CausationId = Guid.NewGuid().ToString("N"),
        BucketId = 3,
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ActorTenantId = tenantId,
        ActorUserId = Guid.NewGuid(),
        Hash = hashed ? [1, 2, 3] : null,
    };

    private static EventChainAnchor Anchor(Guid tenantId, long sequenceNumber) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        BucketId = 3,
        SequenceNumber = sequenceNumber,
        AnchorHash = [(byte)sequenceNumber],
        Timestamp = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// A write context that filters every tenant-scoped entity to its ambient tenant, the way the tenant-isolation guide
    /// switches the filter on. Without a session its ambient tenant is the empty identifier. SQLite compares no
    /// date-time offset, so the timestamp the hash chain's cut-off reads is stored as a number.
    /// </summary>
    private sealed class TenantFilteredWriteDbContext(DbContextOptions<TenantFilteredWriteDbContext> options)
        : WriteDbContext<TenantFilteredWriteDbContext>(options), ITenantScopedDbContext
    {
        public Guid TenantId { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<EventStreamEntry>().Property(e => e.Timestamp).HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.ApplyGlobalTenantQueryFilters(this);
        }
    }
}
