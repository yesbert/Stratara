using Stratara.Abstractions.Outbox;
using Stratara.Contracts.Messages;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The members the intent store gained in 4.2.0 default to what a store written against 4.1.x did: the record's time
/// is the store's own, a resumed hand-over is renewed and runs, a conflict is recorded as a failure, and a stop gives
/// no attempt back.
/// </summary>
public sealed class IntentStoreDefaultsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Recording_with_the_dispatch_time_records_through_the_overload_without_it()
    {
        ICommandIntentStore store = new OldStore();
        var id = Guid.NewGuid();
        var aggregate = Guid.NewGuid();

        await store.RecordAsync(id, new CommandEnvelope(id, "{}", "Probe", "{}"), aggregate, heavy: true, Now.AddHours(-1), CancellationToken.None);

        Assert.Equal([$"record {id} {aggregate} True"], ((OldStore)store).Calls);
    }

    [Fact]
    public async Task A_renewal_from_a_claim_renews_and_takes_the_hand_over()
    {
        ICommandIntentStore store = new OldStore();
        var id = Guid.NewGuid();

        Assert.True(await store.TryRenewFromAsync(id, Now.AddMinutes(-1), Now, CancellationToken.None));
        Assert.Equal([$"renew {id} {Now:O}"], ((OldStore)store).Calls);
    }

    [Fact]
    public async Task A_conflict_is_recorded_as_a_failure()
    {
        ICommandIntentStore store = new OldStore();
        var id = Guid.NewGuid();

        await store.RecordConflictAsync(id, "conflict", CancellationToken.None);

        Assert.Equal([$"failure {id} conflict"], ((OldStore)store).Calls);
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.RecordConflictAsync(id, null!, CancellationToken.None));
    }

    [Fact]
    public async Task A_stop_gives_nothing_back()
    {
        ICommandIntentStore store = new OldStore();

        await store.ReturnAttemptAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(((OldStore)store).Calls);
    }

    [Fact]
    public void A_recorded_intent_written_without_a_conflict_count_has_none()
    {
        var id = Guid.NewGuid();

        Assert.Equal(0, new RecordedIntent(id, new CommandEnvelope(id, "{}", "Probe", "{}"), null, false, 2, null, null).ConflictCount);
    }

    /// <summary>A store written against 4.1.x: it implements only the members that version required.</summary>
    private sealed class OldStore : ICommandIntentStore
    {
        public List<string> Calls { get; } = [];

        public Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, CancellationToken cancellationToken)
        {
            Calls.Add($"record {intentId} {aggregateId} {heavy}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RecordedIntent>> GetDueAsync(DateTimeOffset handedOverBefore, int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordedIntent>>([]);

        public Task<bool> TryClaimAsync(Guid intentId, DateTimeOffset? expectedLastHandedOverAt, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task RenewAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls.Add($"renew {intentId} {now:O}");
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(Guid intentId, string failure, CancellationToken cancellationToken)
        {
            Calls.Add($"failure {intentId} {failure}");
            return Task.CompletedTask;
        }

        public Task KeepAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
