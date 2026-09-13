using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;
using Xunit.Sdk;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// T2 of the expectations: two transactions insert into one partition, the later one commits first,
/// the reader reads in between, and the earlier one commits afterwards. A reader that skips the late
/// committer has lost an event for good. The baselines are expected to skip; the readers behind the
/// port are expected never to.
/// </summary>
/// <remarks>
/// The second writer commits on a background task with a timeout, because under the portable
/// counter it cannot commit before the first: the counter row it needs is locked. That is the
/// mechanism under test, not a defect of the harness, and the harness must not wait on it.
/// </remarks>
[Collection(InfrastructureCollection.Name)]
public sealed class InterleavedCommitTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_commit_order";
    private const int RandomisedCases = 200;
    private static readonly TimeSpan LongHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SecondWriterGrace = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The partition counter is on only for the reader that orders by it. With the counter on, the
    /// second writer cannot commit first — its counter update waits on the first writer's row lock —
    /// so the interleaving never happens and every reader looks correct. That is the counter's
    /// mechanism, and the baselines and the native reader are measured without it.
    /// </summary>
    public static TheoryData<ReaderCase> Readers => new()
    {
        new ReaderCase("naive",
            store => new NaiveSequenceReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options)),
            MaintainCounter: false, SkipsInFastCase: true, SkipsUnderLongHold: true),
        new ReaderCase("safety-window",
            store => new SafetyWindowReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options), TimeProvider.System),
            MaintainCounter: false, SkipsInFastCase: false, SkipsUnderLongHold: true),
        new ReaderCase("portable-counter",
            store => new PortableCounterReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options)),
            MaintainCounter: true, SkipsInFastCase: false, SkipsUnderLongHold: false),
        new ReaderCase("postgres-transaction-id",
            store => new PostgresTransactionIdReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options)),
            MaintainCounter: false, SkipsInFastCase: false, SkipsUnderLongHold: false),
    };

    private Task<PocStore<PocCommitOrderWriteDbContext>> StoreFor(ReaderCase reader) =>
        PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(Database),
            options => options.MaintainPartitionCounter = reader.MaintainCounter);

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task Deterministic_interleaving_fast(ReaderCase reader)
    {
        await using var store = await StoreFor(reader);
        var skipped = await RunInterleavingAsync(store, reader.Create(store), holdBeforeRead: TimeSpan.Zero, settle: TimeSpan.Zero);

        AssertSkipExpectation(reader.Name, expected: reader.SkipsInFastCase, skipped);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task Deterministic_interleaving_under_a_long_hold(ReaderCase reader)
    {
        await using var store = await StoreFor(reader);
        var skipped = await RunInterleavingAsync(store, reader.Create(store), holdBeforeRead: LongHold, settle: TimeSpan.Zero);

        AssertSkipExpectation(reader.Name, expected: reader.SkipsUnderLongHold, skipped);
    }

    /// <summary>
    /// The case the hand-off's sketch missed: B's transaction takes its id before A inserts, so B
    /// holds the lower transaction id and the higher sequence number. A reader that orders by
    /// sequence number and filters by the transaction horizon loses A here; ordering by transaction
    /// id does not.
    /// </summary>
    [Theory]
    [MemberData(nameof(Readers))]
    public async Task Deterministic_interleaving_with_reversed_transaction_ids(ReaderCase reader)
    {
        await using var store = await StoreFor(reader);
        var sut = reader.Create(store);
        var tenantId = Guid.NewGuid();
        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var position = await DrainAsync(sut, partition, 0, []);

        var entryA = PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, tenantId);
        var entryB = PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, tenantId);

        await using var contextB = await store.CreateContextAsync();
        await using var transactionB = await contextB.Database.BeginTransactionAsync();
        await contextB.Database.ExecuteSqlRawAsync("SELECT pg_current_xact_id()");

        await using var contextA = await store.CreateContextAsync();
        await using var transactionA = await contextA.Database.BeginTransactionAsync();
        contextA.Set<EventStreamEntry>().Add(entryA);
        await contextA.SaveChangesAsync();

        var secondWriter = Task.Run(async () =>
        {
            contextB.Set<EventStreamEntry>().Add(entryB);
            await contextB.SaveChangesAsync();
            await transactionB.CommitAsync();
        });
        await Task.WhenAny(secondWriter, Task.Delay(SecondWriterGrace));

        var seen = new HashSet<Guid>();
        position = await DrainAsync(sut, partition, position, seen);

        await transactionA.CommitAsync();
        await secondWriter;
        await Task.Delay(store.Options.SafetyWindow + TimeSpan.FromMilliseconds(20));
        await DrainAsync(sut, partition, position, seen);

        AssertSkipExpectation(reader.Name, expected: reader.SkipsInFastCase, skipped: !seen.Contains(entryA.Id));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task Randomised_interleavings(ReaderCase reader)
    {
        await using var store = await StoreFor(reader);
        var sut = reader.Create(store);
        var random = new Random(20260913);
        var skips = 0;

        for (var i = 0; i < RandomisedCases; i++)
        {
            var hold = TimeSpan.FromMilliseconds(random.Next(0, 50));
            var settle = TimeSpan.FromMilliseconds(random.Next(0, 50));
            if (await RunInterleavingAsync(store, sut, hold, settle))
            {
                skips++;
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{reader.Name}: {skips} of {RandomisedCases} randomised interleavings skipped an entry");

        if (reader is { SkipsInFastCase: false, SkipsUnderLongHold: false })
        {
            Assert.Equal(0, skips);
        }
    }

    /// <summary>
    /// Returns whether the reader lost the entry that committed last. Writer A inserts and holds its
    /// transaction; writer B inserts and commits on its own task; after <paramref name="holdBeforeRead"/>
    /// the reader reads and advances; A commits; B is awaited; after <paramref name="settle"/> the
    /// reader drains. A is lost if it never shows up.
    /// </summary>
    private static async Task<bool> RunInterleavingAsync(
        PocStore<PocCommitOrderWriteDbContext> store,
        ICommittedPositionReader reader,
        TimeSpan holdBeforeRead,
        TimeSpan settle)
    {
        var tenantId = Guid.NewGuid();
        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var position = await DrainAsync(reader, partition, 0, []);

        var entryA = PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, tenantId);
        var entryB = PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, tenantId);

        await using var contextA = await store.CreateContextAsync();
        await using var transactionA = await contextA.Database.BeginTransactionAsync();
        contextA.Set<EventStreamEntry>().Add(entryA);
        await contextA.SaveChangesAsync();

        var secondWriter = Task.Run(async () =>
        {
            await using var contextB = await store.CreateContextAsync();
            await using var transactionB = await contextB.Database.BeginTransactionAsync();
            contextB.Set<EventStreamEntry>().Add(entryB);
            await contextB.SaveChangesAsync();
            await transactionB.CommitAsync();
        });
        await Task.WhenAny(secondWriter, Task.Delay(SecondWriterGrace));

        await Task.Delay(holdBeforeRead);
        var seen = new HashSet<Guid>();
        position = await DrainAsync(reader, partition, position, seen);

        await transactionA.CommitAsync();
        await secondWriter;

        await Task.Delay(settle);
        await Task.Delay(store.Options.SafetyWindow + TimeSpan.FromMilliseconds(20));
        await DrainAsync(reader, partition, position, seen);

        return !seen.Contains(entryA.Id);
    }

    private static async Task<long> DrainAsync(ICommittedPositionReader reader, int partition, long position, HashSet<Guid> seen)
    {
        while (true)
        {
            var batch = await reader.ReadAfterAsync(partition, position, 100);
            if (batch.Entries.Count == 0)
            {
                return batch.Position;
            }

            foreach (var entry in batch.Entries)
            {
                seen.Add(entry.Entry.Id);
            }

            position = batch.Position;
        }
    }

    private static void AssertSkipExpectation(string reader, bool expected, bool skipped)
    {
        if (expected && !skipped)
        {
            throw new XunitException(
                $"The {reader} reader did not skip the late committer, so this test no longer provokes the interleaving it exists for. Repair the test before trusting any other reader's result.");
        }

        if (!expected && skipped)
        {
            throw new XunitException($"The {reader} reader skipped the late committer — an event was lost.");
        }
    }

    public sealed record ReaderCase(
        string Name,
        Func<PocStore<PocCommitOrderWriteDbContext>, ICommittedPositionReader> Create,
        bool MaintainCounter,
        bool SkipsInFastCase,
        bool SkipsUnderLongHold)
    {
        public override string ToString() => Name;
    }
}
