# Let a host wait until a projection has applied what is committed

> **Status:** approved

## Why

Under the Orleans execution model every projection reads the store from a checkpoint per partition, so
a read model follows a write shortly after it, never at once. A host that has to act on its read model
right after a write — refuse an operation while a row names something, answer a request only once the
list it returns shows the caller's own change — cannot find out whether the projection is through. It
can only read and hope.

A consumer reported it on 2026-10-22 (consumer finding SF-010), with one broken guarantee of its own
behind it: a refusal that reads a read model lets through a request a write a moment earlier should
have stopped.

The host cannot build the wait itself, because everything it would need is the framework's:

- Positions are per partition and meaningful only to the reader that produced them; the checkpoint is
  keyed by the projection's framework name, the partition and the reader's name, none of which a host
  is meant to know.
- The one public "where is the store" answer, a reader's head, does not mean what the wait needs. The
  native PostgreSQL reader holds its head back to what no open transaction can precede, so while other
  writers run the head can lie *below* an entry that is already committed and visible. A checkpoint at
  that head proves nothing about that entry.
- Polling the read model for an expected row cannot tell "not applied yet" from "never coming".

## What Changes

The consumer-visible effect: a host that runs its projections on the execution model can take a mark
of the store and wait, with a time limit, until a named projection has applied everything the mark
covers — and is told plainly when it has not.

- **New abstraction `IProjectionProgress`** (`Stratara.Abstractions`), registered by
  `AddStrataraProjectionGrains` beside `IProjectionRebuilder`:
  - `MarkAsync` returns a `StoreMark`: an opaque value that covers every entry committed at the moment
    of the call, in every partition — including an entry above a held-back head.
  - `WaitAsync` takes a projection (by its framework name or its type), a mark and a time limit, and
    answers with a `ProjectionWaitResult` once the projection has applied every entry the mark covers
    in every partition, or once the limit has passed.
- **The answer says what happened.** `Reached` only when every partition is through. Otherwise, at the
  limit: `Stalled` where a partition is stopped at an entry it cannot apply or cannot read the store,
  `Suspended` where its readers are paused for a rebuild or a replay is active, `TimedOut` where it is
  merely behind — each with the partitions that did not get there. A mark taken under another reader
  or partition count than the host's now answers `MarkNotApplicable` at once. A wait never returns as
  though it succeeded when it did not.
- **A rebuild or a full replay during the wait is waited through**, not reported as reached: the
  checkpoint returning to the beginning simply puts the mark ahead again.
- **`ICommittedPositionReader` gains `CommittedHeadAsync`**, the highest position the call can see as
  committed — the framework's native PostgreSQL reader answers it without holding it back; every other
  reader, the portable counter included, answers its existing head, which already means that.
- **Documentation**: how a host waits for its own write, what a mark covers and what it does not
  (a transaction still open when it is taken), and what each answer means.

Not changed: how projections read, apply and checkpoint; how a commit wakes them; the bus model, which
has no checkpoint and gains nothing here; store-reading sagas. A host that never resolves the new
service pays nothing for it.

Deferred: a mark of one commit — taken from the append itself, covering only the partitions it touched.
A host gets the same guarantee by taking the store's mark after its command returned; the narrower mark
is an optimisation that can follow without changing anything this change adds.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: gains *A host can wait until a projection has applied what was committed* — the
  mark, the wait, its answers, the waits through a rebuild and a replay, and the documentation.

## Impact

- `src/Stratara.Abstractions/Abstractions/Projections/IProjectionProgress.cs` — new, with `StoreMark`,
  `ProjectionWaitResult`, `ProjectionPartitionLag` and `ProjectionWaitOutcome`.
- `src/Stratara.Abstractions/Abstractions/CommitOrder/ICommittedPositionReader.cs` — new member
  `CommittedHeadAsync` with a default that answers `HeadAsync`.
- `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PostgresTransactionIdReader.cs` — overrides it
  with a statement that drops the horizon filter.
- `src/Stratara.Orleans/Projections/` — `ProjectionProgress` (new), `StoreReaderGrain` and
  `StoreReaderLoop` (a waiter call per partition that answers once the checkpoint reaches a position or
  its own deadline passes, with the reader's state), `IProjectionGrain` (one new grain method),
  `StoreReaderProgress.cs` (new: the grain's answer and the reader's state, serialized between silos).
- `src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs` — registers
  the service.
- `docs/guides/write-a-projection.md`, `docs/guides/operate-the-orleans-execution-model.md`,
  `docs/concepts/orleans-execution-model.md`, `llms.txt`, `CHANGELOG.md`.
- Additive: new public API only, no package, dependency, tier or schema change, no migration. A patch
  release (4.4.6).
- Rolling upgrade: a silo of the previous release does not answer the new grain method; a wait whose
  partition lives on such a silo fails with the grain call's error rather than answering. Hosts adopt
  the wait once every silo runs this release (`design.md`, *Migration Plan*).
- Dissolved or superseded legacy sources: none.
