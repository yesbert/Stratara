## Context

See `proposal.md` — *Why*. What shapes the approach is what the execution model already knows and where
it knows it. Every point below was read on `main` at `67dc562` (`v4.4.5`).

- **Positions are per partition and per reader.** A checkpoint is keyed by consumer, partition and reader
  name (`IProjectionCheckpointStore`), the reader's name carries the partition count
  (`postgres-transaction-id/{n}`, `partition-counter/{n}`), and the consumer name of a projection is
  `projection.GetType().Name` (`ProjectionHandler.cs:48`). The partition count is
  `CommitOrderOptions.PartitionCount`, default 16.
- **The native reader holds its head back.** `PostgresTransactionIdReader` reads and answers its head only
  below `pg_snapshot_xmin(pg_current_snapshot())` (the `ReadAfter` and `Head` statements). That is what
  keeps the port's promise — nothing at or below a returned position can still commit — and it is why a
  head taken while another writer is open can lie below an entry that is already visible.
  `ICommittedPositionReader.HeadAsync` says so in its remarks.
- **The portable reader's head is exact.** `PortableCounterReader.HeadAsync` is `MAX(partition_position)`;
  the counter row is locked from increment to commit, so every position at or below a visible one is
  committed.
- **A reader applies in position order and never skips** (the requirement *Projections and sagas read
  the store in commit order and never miss a committed fact*). Its checkpoint at or above position X
  therefore means every entry at or below X has been applied.
- **The grain is the authority on a partition's progress.** `StoreReaderLoop` keeps the position it last
  wrote (`_position`), knows whether it is stopped at an entry or on a read (`_stalledOnEntry`,
  `_stalledOnRead`), and `StoreReaderGrain.Suspended` is `Pausers.Any || replayState.IsReplayActive`.
  None of that reaches the checkpoint store; only the position does.
- **A rebuild moves checkpoints behind the grain's back** and tells it through `Invalidate`
  (`ProjectionRebuilder`, `StoreReaderLoop.Invalidate`). A full replay does the same through
  `ReplayCheckpointReset`.
- **`IProjectionRebuilder` is the precedent** for a host-facing service of the execution model: an
  interface in `Stratara.Abstractions`, implemented in `Stratara.Orleans`, registered by
  `AddStrataraProjectionGrains`, projection named by string, an unknown name refused with
  `InvalidOperationException`.

## Goals / Non-Goals

**Goals:**

- A mark that covers what is committed and visible, with both of the framework's readers.
- A wait whose only success answer is true, and whose other answers tell slow from stuck from paused.
- No cost on any path for a host that does not wait; no polling load that grows with the number of
  waiters times the partition count.

**Non-Goals:**

- A mark of one commit (proposal, *Deferred*).
- Carrying a mark across processes — serializing it into a response, a header or a token. It is an
  in-process value; making it portable is additive and can follow.
- Waiting for store-reading sagas. The same mechanism would serve them; nobody asked, and a saga's
  "applied" also means "its commands were enqueued", which deserves its own statement.
- The bus model. It has no checkpoint to compare a mark with.

## Decisions

### D1 — The mark is the highest committed position each partition shows the call, not the head

The mark is a vector: per partition, the highest position the store shows as committed when it is read,
plus the reader's name. A new member on the port gives it:

```csharp
// ICommittedPositionReader
Task<long> CommittedHeadAsync(int partition, CancellationToken cancellationToken = default)
    => HeadAsync(partition, cancellationToken);
```

The native reader overrides it with its `Head` statement minus the horizon filter — `MAX(transaction_id)`
over the partition's visible rows. Under read committed the statement's snapshot sees every transaction
committed before it started, so the value covers everything committed when the host called. The reader
reaches that value only after every lower transaction has ended, and its checkpoint at or above it
proves every visible entry is applied (*Context*, never skips).

*Why a new member, not a parameter on `HeadAsync`:* `HeadAsync` has a use — seeding a consumer at the
head — for which the held-back value is the right one. *Why the default answers `HeadAsync`:* the summary
of `HeadAsync` is exactly the promise the mark needs ("no entry committed at the time of the call exists"
after it); only a reader that documents holding it back, as the native one does, needs to override. A
default that throws, as `IProjectionCheckpointStore.FindAsync` does, was rejected: it would break every
host-supplied reader that keeps the promise for a member it never needed to know about.

*Alternative rejected — a transaction horizon* (`pg_current_snapshot()` itself as the mark). Exact and
one statement, but meaningless to the portable reader and to any other; the vector works for every
reader through the port.

One statement per partition, run in sequence in one scope: sixteen indexed `MAX` reads at the default
count. A grouped statement is a later optimisation that changes nothing a host sees.

Evidence: the two readers' statements; the requirement on commit order.

### D2 — `IProjectionProgress` in `Stratara.Abstractions`, beside `IProjectionRebuilder`

```csharp
namespace Stratara.Abstractions.Projections;

public interface IProjectionProgress
{
    Task<StoreMark> MarkAsync(CancellationToken cancellationToken = default);
    Task<ProjectionWaitResult> WaitAsync(string projectionName, StoreMark mark, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<ProjectionWaitResult> WaitAsync(Type projectionType, StoreMark mark, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class StoreMark { /* reader name + positions; no public members beyond ToString */ }

public enum ProjectionWaitOutcome { Reached, TimedOut, Stalled, Suspended, MarkNotApplicable }

public sealed record ProjectionPartitionLag(int Partition, ProjectionWaitOutcome State);

public sealed record ProjectionWaitResult(ProjectionWaitOutcome Outcome, IReadOnlyList<ProjectionPartitionLag> Behind)
{
    public bool IsReached => Outcome == ProjectionWaitOutcome.Reached;
}
```

Tier A cannot name `IProjection`, so the type overload takes a `Type`; the implementation resolves the
registered projection of exactly that type and asks the handler for its name — the host never repeats
the naming rule. The overall outcome at the limit is the most telling partition state: `Stalled` over
`Suspended` over `TimedOut`.

*Why a result, not an exception, for a timeout:* running out of time is an expected answer the caller
branches on (refuse with "try again"); an unknown projection and a negative timeout are programming
errors and throw (`InvalidOperationException`, `ArgumentOutOfRangeException`). A zero timeout is a check
without waiting.

Evidence: `IProjectionRebuilder` and its registration.

### D3 — The wait asks each partition's grain, which answers when its checkpoint gets there

A new grain method on `IProjectionGrain`, `[AlwaysInterleave]` so it never queues behind a catch-up:

```csharp
Task<StoreReaderProgress> WaitForAsync(long position, TimeSpan budget);
// StoreReaderProgress(long Position, StoreReaderState State) — Advancing, StalledOnEntry, StalledOnRead, Suspended, Retired
```

The grain requests a catch-up (so the reader reads now, not at its next poll), registers the waiter, and
completes it when the loop has advanced the checkpoint to `position` or past it, or when `budget` runs
out, with the loop's state. `StoreReaderLoop` signals after every checkpoint it writes. A waiting call
holds its grain's deactivation until it is answered; a waiter still registered when the deactivation
runs is answered with the state, and the caller asks the next activation.

`ProjectionProgress.WaitAsync` checks the reader name first (D5), counts a partition whose mark is `0`
as reached, and calls the grain of every other partition in parallel, in rounds, with a budget of the
remaining time capped at two seconds. It answers `Reached` only when every partition answered reached
in the same round; a partition that is through answers at once, so a round costs little.

*Amended during review (2026-10-22), before merge.* The plan was a ten-second cap, and a partition
that had answered reached was not asked again. The review of the implementation found two faults:
a partition counted reached before a rebuild began was never re-checked, so a rebuild during the wait
could leave it emptied under a `Reached` answer; and a waiting call holds its grain's deactivation for
its whole budget, because the runtime lets in-flight requests finish before it deactivates. Asking
every partition each round closes the first — a rebuild pauses every reader before it empties
anything, and a paused reader never answers reached (D4), so one round in which all answer reached
leaves no rebuild in progress behind it. The two-second cap bounds the second, and also keeps a call
far below any response timeout a host is likely to set.

*Alternative rejected — poll the checkpoint store.* Correct and simple, but it cannot see a stall or a
pause, and its load grows with waiters × partitions × frequency, on the read store every projection
writes to. *Alternative rejected — poll the grain.* Sees the state, keeps the load.

Evidence: `StoreReaderGrain` (`Suspended`, `NudgeAsync`, `PositionAsync`), `StoreReaderLoop`
(`ReadAndApplyAsync` writes every checkpoint through `AdvanceAsync`).

### D4 — A suspended reader never answers "reached"

The grain answers with its position, but the wait counts a partition as reached only where its state is
not `Suspended`. A reader paused for a rebuild may still hold a position from before the truncation; a
reader whose pause lapsed may have advanced over a model about to be emptied (the requirement on
rebuilds describes both). Answering "reached" there could hand the host a read model that is emptied
the next moment. Holding the answer until the pause ends makes *The projection is rebuilt during the
wait* hold without the wait knowing anything about rebuilds.

### D5 — A mark names its reader; another reader's mark is not applicable

The mark carries the reader name it was taken under. `WaitAsync` compares it with the host's current
reader name and answers `MarkNotApplicable` at once on a difference: positions under another partition
count or another ordering mean nothing here, and comparing them could answer "reached" falsely. A mark
lives for one request in practice, so the case is a deployment changing under a waiting request.

### D6 — A stall does not end the wait early

`StalledOnEntry` is also the state of an entry waiting out the preceding-fact policy, which a retry
resolves within seconds. Ending the wait at the first stall would turn "slow" into "stuck". The state is
reported when the limit passes, which is when the host has to decide anyway.

## Risks / Trade-offs

- [A long-running transaction that appends nothing holds the native reader's horizon back, so a wait
  times out though the host's fact is long visible] → the answer is `TimedOut` (or `Stalled`), never a
  false `Reached`; the operating guide names the cause, and the reader's lag gauge already shows it.
- [The mark costs one query per partition] → indexed `MAX` reads; a host that marks on every request
  pays sixteen small reads by default. Documented; a grouped statement can replace it without changing
  the API.
- [A waiter registered on an activation that moves] → completed with the state at deactivation and asked
  again; at most one extra round trip.
- [A host-supplied reader that holds its head back but does not override `CommittedHeadAsync` gets a
  mark below what is committed] → the member's documentation says when to override; the framework's
  readers are both covered by tests.

## Migration Plan

Additive; no schema change and no migration. In a cluster upgraded silo by silo, a silo of 4.4.5 does
not know `WaitForAsync`, and a wait whose partition is activated there fails with the grain call's error
rather than answering. The guide says to adopt the wait once every silo runs 4.4.6. Rollback: a host
that stops calling the service is back to the previous behaviour; nothing is stored.

## Open Questions

- Whether the operating guide should recommend a default time limit, or leave it to the host's latency
  budget. Documentation only.
