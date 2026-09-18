using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.Outbox;
using Stratara.Contracts.Messages;
using Stratara.Orleans.EntityFrameworkCore.Intents;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// The intent store on PostgreSQL: of two renewals from one claim's stamp exactly one takes the hand-over over, a
/// renewal of a record that is gone touches nothing, commands recorded at the same time are due in the order of their
/// identity, a conflict gives its attempt back and a stop returns one without either count going below zero, and a
/// record takes its time from the registered clock.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class IntentFenceTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_intent_fence";

    [Fact]
    public async Task Of_two_renewals_from_one_stamp_exactly_one_takes_the_hand_over_and_moves_the_stamp()
    {
        await using var store = await EmptyStoreAsync();
        var intents = new CommandIntentStore<PocWriteDbContext>(store.ContextFactory, TimeProvider.System);
        var id = await RecordAsync(intents, DateTimeOffset.UtcNow.AddMinutes(-1));
        var stamp = Truncated(DateTimeOffset.UtcNow);
        Assert.Equal([id], await intents.ClaimAsync(await DueAsync(intents), stamp, TestContext.Current.CancellationToken));

        // The receivers' clocks may lag the claimer's: a renewal whose own time is not later still moves the stamp.
        var renewals = await Task.WhenAll(
            intents.TryRenewFromAsync(id, stamp, stamp.AddSeconds(-5), TestContext.Current.CancellationToken),
            intents.TryRenewFromAsync(id, stamp, stamp.AddSeconds(-5), TestContext.Current.CancellationToken));

        Assert.Single(renewals, renewed => renewed);
        var entry = await EntryAsync(store, id);
        Assert.Equal(stamp.AddMilliseconds(1), entry.LastHandedOverAt);
        Assert.Equal(1, entry.AttemptCount);
        Assert.False(await intents.TryRenewFromAsync(id, stamp, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_renewal_of_a_command_that_completed_touches_nothing()
    {
        await using var store = await EmptyStoreAsync();
        var intents = new CommandIntentStore<PocWriteDbContext>(store.ContextFactory, TimeProvider.System);
        var id = await RecordAsync(intents, DateTimeOffset.UtcNow.AddMinutes(-1));
        var stamp = Truncated(DateTimeOffset.UtcNow);
        await intents.ClaimAsync(await DueAsync(intents), stamp, TestContext.Current.CancellationToken);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Set<OutboxEntry>().Where(e => e.Id == id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        Assert.False(await intents.TryRenewFromAsync(id, stamp, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Commands_recorded_at_the_same_time_are_due_in_the_order_of_their_identity_after_an_earlier_one()
    {
        await using var store = await EmptyStoreAsync();
        var intents = new CommandIntentStore<PocWriteDbContext>(store.ContextFactory, TimeProvider.System);
        var at = Truncated(DateTimeOffset.UtcNow.AddMinutes(-1));
        var tied = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).OrderByDescending(id => id.ToString(), StringComparer.Ordinal).ToList();
        foreach (var id in tied)
        {
            await RecordAsync(intents, at, id);
        }

        var earliest = await RecordAsync(intents, at.AddMilliseconds(-1));

        var due = await DueAsync(intents);

        Assert.Equal([earliest, .. tied.OrderBy(id => id.ToString(), StringComparer.Ordinal)], due.Select(intent => intent.Id));
    }

    [Fact]
    public async Task A_conflict_gives_its_attempt_back_and_a_stop_returns_one_and_neither_count_goes_below_zero()
    {
        await using var store = await EmptyStoreAsync();
        var intents = new CommandIntentStore<PocWriteDbContext>(store.ContextFactory, TimeProvider.System);
        var id = await RecordAsync(intents, DateTimeOffset.UtcNow.AddMinutes(-1));
        var ct = TestContext.Current.CancellationToken;

        await intents.RecordConflictAsync(id, "first conflict", ct);
        await intents.ReturnAttemptAsync(id, ct);
        var untouched = await EntryAsync(store, id);
        Assert.Equal((0, 1, "first conflict"), (untouched.AttemptCount, untouched.ConflictCount, untouched.LastFailure));

        await intents.ClaimAsync(await DueAsync(intents), Truncated(DateTimeOffset.UtcNow), ct);
        await intents.RecordConflictAsync(id, "second conflict", ct);
        var conflicted = await EntryAsync(store, id);
        Assert.Equal((0, 2, "second conflict"), (conflicted.AttemptCount, conflicted.ConflictCount, conflicted.LastFailure));

        var due = await intents.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10, ct);
        Assert.Equal(2, Assert.Single(due).ConflictCount);
        await intents.ClaimAsync(due, Truncated(DateTimeOffset.UtcNow.AddMilliseconds(5)), ct);
        await intents.ReturnAttemptAsync(id, ct);
        await intents.ReturnAttemptAsync(id, ct);
        Assert.Equal(0, (await EntryAsync(store, id)).AttemptCount);
    }

    [Fact]
    public async Task A_record_takes_its_time_from_the_registered_clock()
    {
        await using var store = await EmptyStoreAsync();
        var clock = new SettableClock(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var intents = new CommandIntentStore<PocWriteDbContext>(store.ContextFactory, clock);
        var id = Guid.CreateVersion7();

        await intents.RecordAsync(id, new CommandEnvelope(id, "{}", "Probe", "{}"), Guid.NewGuid(), heavy: false, TestContext.Current.CancellationToken);

        Assert.Equal(clock.GetUtcNow(), (await EntryAsync(store, id)).Timestamp);
        Assert.Empty(await intents.GetDueAsync(clock.GetUtcNow().AddSeconds(-30), 10, TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal([id], (await intents.GetDueAsync(clock.GetUtcNow().AddSeconds(-30), 10, TestContext.Current.CancellationToken)).Select(intent => intent.Id));
    }

    private async Task<PocStore<PocWriteDbContext>> EmptyStoreAsync()
    {
        var store = await PocStore<PocWriteDbContext>.CreateAsync(postgres.ConnectionStringFor(Database));
        await using var context = await store.CreateContextAsync();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM outbox_entry", TestContext.Current.CancellationToken);
        return store;
    }

    private static async Task<Guid> RecordAsync(CommandIntentStore<PocWriteDbContext> intents, DateTimeOffset recordedAt, Guid? intentId = null)
    {
        var id = intentId ?? Guid.CreateVersion7();
        await intents.RecordAsync(id, new CommandEnvelope(id, "{}", "Probe", "{}"), Guid.NewGuid(), heavy: false, recordedAt, TestContext.Current.CancellationToken);
        return id;
    }

    private static Task<IReadOnlyList<RecordedIntent>> DueAsync(CommandIntentStore<PocWriteDbContext> intents) =>
        intents.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1), 100, TestContext.Current.CancellationToken);

    private static async Task<OutboxEntry> EntryAsync(PocStore<PocWriteDbContext> store, Guid id)
    {
        await using var context = await store.CreateContextAsync();
        return await context.Set<OutboxEntry>().AsNoTracking().SingleAsync(e => e.Id == id, TestContext.Current.CancellationToken);
    }

    private static DateTimeOffset Truncated(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    /// <summary>A clock that stands where the test puts it.</summary>
    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
