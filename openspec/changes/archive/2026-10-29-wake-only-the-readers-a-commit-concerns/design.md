## Context

See proposal.md, *Why*. The code as of `f76fa6d` (4.6.0):

- **The commit's wake-up names partitions only.** `OrleansEventBundleDispatcher.EnqueueEventBundleAsync` folds the
  bundle's streams into partitions and calls `LocalNudges.NudgeAsync`. That calls `INudgeTarget.NudgeAsync` for every
  target and partition, and `ProjectionNudgeTarget` / `SagaNudgeTarget` send `NudgeAsync` to the grain of *every*
  registered name. `ClusterWakeUps.Wake` sends the buckets to `IProjectionWakeUpGrain` / `ISagaWakeUpGrain.WakeAsync(int[])`,
  which does the same on the silo.
- **The reader reads everything and checkpoints everything.** `StoreReaderLoop.ReadAndApplyAsync` reads
  `ReadAfterAsync(partition, _position, batchSize)` with no type filter, hands the batch to the grain, and calls
  `checkpoints.AdvanceAsync(…, _position, batch.Position)` once the batch is through. `ProjectionGrain.ApplyBatchAsync`
  filters by `_relevant` and `EventRelevance` only inside the per-entry callback, and an entry with no relevant event
  counts as applied. `SagaReaderGrain` does the same.
- **Each reader polls on its own.** `StoreReaderGrain` registers a grain timer at `PollInterval` (default 5 s), and
  `RequestPoll` starts a catch-up whenever none is running, which is at least one `ReadAfterAsync` per reader and tick.
- **Waits already use the in-memory position.** `StoreReaderGrain.HasReached` asks `Loop.TryGetPosition`, so deferring
  the checkpoint write does not hold up `IProjectionProgress`.
- **Things that read stored checkpoints:** `StoreReaderLoop.EnsurePositionAsync` (activation, after `Invalidate`),
  `SagaStart.EnsureAsync` (a new saga starts at the furthest checkpoint the host's sagas hold), `StoreReaderSeeding`,
  and the rebuild and replay resets, which write the checkpoint to 0 while the readers are paused.
- **Head queries:** both shipped readers, `PostgresTransactionIdReader` and `PortableCounterReader`, override
  `ICommittedPositionReader.HeadAsync` with one query. The interface's default walks the partition.

## Goals / Non-Goals

**Goals:**

- The work per commit scales with the consumers that handle its events, not with all of them.
- An idle silo's store load scales with the partition count, not with consumers × partitions.
- Nothing applied changes. No fact is missed, and a crash costs at most a re-read of entries that apply nothing.

**Non-Goals:**

- Filtering by event type in the store read itself (D6).
- Changing the checkpoint's stored form, its fencing, or the poll interval's default.
- The bus path, which has its own subscriptions.

## Decisions

### D1 — The silo decides relevance per consumer, from the committed type names

`ProjectionRegistrations` already builds every projection once to learn its name (4.4.10). It also keeps each
projection's relevant event type names, the same set `ProjectionGrain` matches in `_relevant`
(`handler.GetRelevantEventTypeNames(projection)`). `SagaRegistrations` does the same for sagas. A stateful saga
process (`EventRelevance.IsAnyResolvable`) is marked "always". `INudgeTarget.NudgeAsync` gets the committed type names
(`EventMessage.EventTypeName` of the bundle's events) and nudges a consumer's grain only when one of the names is in
its set, when the consumer is "always", or when a name is undecidable.

The silo decides a name as the mapper does: it upcasts it through the host's `EventUpcasterPipeline` with an empty
payload, which is enough because that pipeline chains upcasters by name, and resolves the name it ends at. A name is
**undecidable**, and wakes every reader, when the type resolver does not know the name it ends at, when an upcaster
refuses the empty payload, when the host's pipeline is its own (it may decide from the payload), or when no resolver
or pipeline is registered. The rule leans to waking: a wrongly woken reader costs one read, a wrongly skipped one costs
poll latency. The names are grouped per partition (per key for the cluster wake-up), so a commit that writes to
several streams wakes, in each partition, only the readers of the events it wrote there. A relevance that cannot be
worked out — a projection whose handler throws for its types, sagas that cannot be built without a session — leaves
that consumer woken by every commit, and is kept that way, so the commit path never pays for the failure again.
*Revised after the code review of 2026-10-29*, which found that the first rule took the upcasters from the
container rather than from the pipeline the mapper uses.
*Evidence:* the matching in `ProjectionGrain.ApplyBatchAsync` (lines 164–175) and `SagaReaderGrain`; task 2.1 checks
how `EventMapperFactory` keys its upcasters, so the undecidable test asks exactly what the mapper would.

*Alternatives:* decide by name only, without the upcaster check. That is cheaper, but a fact written under an
upcast name would wait for the poll. Decide on the host: a client host does not know the silo's projections, so no.

### D2 — The cluster wake-up carries the event types in the request context (revised during implementation, 2026-10-29)

`IProjectionWakeUpGrain.WakeAsync(int[])` and `ISagaWakeUpGrain.WakeAsync(int[])` keep their signature and alias.
`ClusterWakeUps` sets the commit's type names under the request-context key `stratara.wake.event-types` (a
`string[]`) around the calls it issues and restores the previous value afterwards. The wake-up grain reads the key and
filters through D1. Without the key, every reader is woken. That is what a wake-up from a host of 4.6.0 or earlier
looks like, and a silo of 4.6.0 or earlier never reads the key, so it wakes every reader.

*Rejected first plan:* a second method `WakeForAsync(int[], string[])` under a new alias, with a fallback to
`WakeAsync` when the silo does not know it. A silo of the previous version has no invoker for the new method. How
Orleans answers that — an error the caller can match, or a request that times out after the response timeout and
fills the bounded outstanding calls — could not be verified without a cluster of mixed versions, and a timeout would
have turned every wake-up of the rolling window into a lost one plus a held callback. The request context needs no
new method and no fallback. *Evidence:* Orleans propagates `RequestContext` values with the call, and the
receiving grain reads them in its turn. Test 3.3 covers the typed path, and 3.4 the path without the key.

### D3 — The loop separates the position it holds from the one it wrote

`StoreReaderLoop` keeps `_position`, which it reads and answers waits from, and adds `_written`, the position the
checkpoint store holds as far as the loop knows. The batch delegate returns a `BatchOutcome(Processed, Effective)`
instead of an `int`. `Processed` is today's index of the first entry not applied, and `Effective` counts the entries
that handed anything to a handler.

- A whole batch with `Effective > 0` advances from `_written` to `batch.Position` at once, as today.
- A whole batch with `Effective == 0` moves `_position` only, and the deferral clock starts if it is not running.
- A cut batch writes its resume position at once, as today. The cut is about a stall, which must be visible in the
  store.
- At the end of every catch-up and on every poll tick, a deferral older than the interval is written.
  `OnDeactivateAsync` writes it as well, best effort, after the running loop has ended.
- `Invalidate` (pause, replay, refused write, failed catch-up) drops the deferral without writing. Whoever
  invalidates may change the checkpoint behind the loop, so `_written` must never be written over a reset.

`AdvanceAsync` always goes from `_written`, so the fencing (*A stale activation writes a checkpoint*) is unchanged.

The option is `IdleCheckpointInterval`, default 30 s, on `ProjectionGrainOptions` only. `TimeSpan.Zero` writes at
once, which is today's behaviour, and a negative value is refused by the options validator. The loop takes the
interval as a constructor argument. `SagaReaderGrain` passes `TimeSpan.Zero` and keeps writing at once (D7).
*Evidence:* `StoreReaderGrain.HasReached` reads the in-memory position, and the rebuild's reset runs only under a pause,
which calls `Invalidate` (`StoreReaderGrain.RequestPoll` / pause path).

*Alternative:* never write past irrelevant entries until the next relevant one. Then a reader of a rare event re-reads
an unbounded tail after every restart, so no.

### D7 — Saga readers do not defer (found during implementation, 2026-10-29)

The proposal first deferred the checkpoint for saga readers too. Reading `SagaStart.EnsureAsync` showed that a saga
added to a running deployment starts at the furthest checkpoint the host's sagas *hold*. The main spec (*A host can
start its store readers at the head of a populated store*, scenario *A saga is added to a running deployment*)
requires the new saga to react only to facts after the position the host's sagas had reached. A deferred saga
checkpoint would let a new saga run its side effects, which are commands, for up to one interval of history. Saga
readers therefore write at once as before. They still profit from D1, which wakes a saga only when a commit holds
something it declares, and from D4.

*Alternative:* defer, and have `SagaStart` ask the sibling saga grains for their in-memory positions before it creates
a checkpoint. That means cross-grain calls in the start path and a rolling-deployment concern for older silos, which
is more than the saving warrants.

### D4 — An idle poll asks a silo-wide head cache

A singleton `PartitionHeads` per silo holds `(head, takenAt)` per partition. `RequestPoll`, and only the timer's poll,
asks `HasAnythingPast(partition, position)`. When the cached head is older than half of the shortest poll interval
the silo uses, one caller refreshes it through `ICommittedPositionReader.HeadAsync`, single-flight per partition, and
the others await that refresh. A reader with `position >= head` does not start a catch-up.

`HeadAsync` and not `CommittedHeadAsync`: the native reader's `ReadAfterAsync` and `HeadAsync` are held back by the
same horizon, so `position >= HeadAsync` means `ReadAfterAsync` returns nothing. `CommittedHeadAsync` would report
entries the read cannot return yet. A reader that does not override `HeadAsync`, found through the interface map,
keeps polling as before, because the default walks the partition. Nudges, catch-up calls, restarts and waits bypass
the cache. A stalled reader's position is below the head, so it keeps retrying once per poll as today.

The worst case for a lost wake-up is that the fact commits just after a refresh. The next refresh comes half a poll
interval later and the reader's next tick picks it up, so about 1.5 poll intervals. *Evidence:* the head statements in
`PostgresTransactionIdReader` (`Statements.Head`) and `PortableCounterReader.HeadAsync`. Task 1.4 measures the idle
query count before and after.

*Alternative:* one silo-wide poller that nudges readers. It would have to know which readers are activated where, and
placement spreads them over silos. The cache needs no such knowledge.

### D5 — What stays per commit under load

While a partition moves, every reader of it still reads once per poll tick, and the entries are mapped with
`EventRelevance`, which skips deserialising irrelevant ones. With 1,092 readers that is at most about 220 reads a
second, against one read per reader per commit today, and no checkpoint writes for them within the interval.

### D6 — No type filter in the store read

The native read cuts batches at transaction boundaries and returns the resume position from the last row
(`PostgresTransactionIdReader.ReadAfterAsync`). A read that filters out every row of a stretch must still return a
position past that stretch, which needs a second query or a different statement, per reader type, and it changes a
published abstraction. With D1 to D4 the gain is small. If the load under D5 matters, this is the follow-up.

## Risks / Trade-offs

- **Saga readers keep a checkpoint write per batch read.** While a partition moves with facts a saga does not
  handle, its reader writes once per poll tick (D5, D7). → That is bounded by sagas × partitions per poll interval,
  and far below today's write per commit.
- **An undecidable name wakes everyone.** A host that keeps upcasters for many names gets less of D1. → That is still
  correct. The guide names it.
- **A wrong relevance set would hide a fact until the poll.** → D1 reuses the set the grain applies with, and test 1.1
  asserts both sides: the handling reader is woken, and the others are not.
- **Deactivation write may fail.** → The checkpoint stays behind, and the next activation re-reads entries that apply
  nothing. That is the case the spec allows.
- **The head cache is per silo, not per cluster.** A cluster of N silos asks N × partitions questions per refresh.
  That is acceptable, and far below one per reader.

## Migration Plan

Rolling deployment as D2 describes. No stored form changes. Rolling back to 4.6.0 reads the checkpoints as written.
A deferred position that was never written means that version re-reads the entries that apply nothing. No consumer
action is needed, and `IdleCheckpointInterval = TimeSpan.Zero` restores the old checkpoint timing.
