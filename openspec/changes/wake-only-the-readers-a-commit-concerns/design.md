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

A name is **undecidable** when the silo has an upcaster registered for it, or cannot resolve it to an event type.
After upcasting, the reader might map such an event to a type a consumer handles, so every reader is woken. The rule
leans to waking: a wrongly woken reader costs one read, a wrongly skipped one costs poll latency.
*Evidence:* the matching in `ProjectionGrain.ApplyBatchAsync` (lines 164–175) and `SagaReaderGrain`; task 2.1 checks
how `EventMapperFactory` keys its upcasters, so the undecidable test asks exactly what the mapper would.

*Alternatives:* decide by name only, without the upcaster check. That is cheaper, but a fact written under an
upcast name would wait for the poll. Decide on the host: a client host does not know the silo's projections, so no.

### D2 — The cluster wake-up names the event types under a new method, and falls back to the old one

`IProjectionWakeUpGrain` and `ISagaWakeUpGrain` get `WakeForAsync(int[] buckets, string[] eventTypeNames)`
(`[AlwaysInterleave]`, alias `WakeForAsync`), which filters through D1 on the silo. The existing `WakeAsync(int[])`
stays and wakes every reader, so a host of 4.6.0 or earlier still wakes everything. `ClusterWakeUps` calls
`WakeForAsync`. A call that fails because the silo does not know the method is sent again as `WakeAsync`, and the
role remembers to use `WakeAsync` for `StoreWakeUpOptions.AbsentRoleRetry` before it tries the new form again. The
remembering works like the absent-role set-aside, with its own generation.
*Evidence to obtain:* task 3.2 records which exception Orleans 10.4 raises for a method the target silo's interface
lacks, and the fallback matches that one only.

*Alternative:* bump the grain interface version. That forces Orleans' versioning policy on every host, which costs
more than one fallback.

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
  `OnDeactivateAsync` writes it as well, best effort.
- `Invalidate` (pause, replay, refused write, failed catch-up) drops the deferral without writing. Whoever
  invalidates may change the checkpoint behind the loop, so `_written` must never be written over a reset.

`AdvanceAsync` always goes from `_written`, so the fencing (*A stale activation writes a checkpoint*) is unchanged.

The option is `IdleCheckpointInterval`, default 30 s, on `ProjectionGrainOptions` and `SagaGrainOptions`.
`TimeSpan.Zero` writes at once, which is today's behaviour, and a negative value is refused by the options validator.
*Evidence:* `StoreReaderGrain.HasReached` reads the in-memory position, and the rebuild's reset runs only under a pause,
which calls `Invalidate` (`StoreReaderGrain.RequestPoll` / pause path).

*Alternative:* never write past irrelevant entries until the next relevant one. Then a reader of a rare event re-reads
an unbounded tail after every restart, so no.

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

- **A saga added later may start a little earlier.** `SagaStart.EnsureAsync` starts a new saga at the furthest
  *written* checkpoint of the host's sagas. Deferral can leave that up to one `IdleCheckpointInterval` behind, so a
  saga added to a running deployment may apply facts committed in that window before its start. That is the same as
  when its siblings lag behind today. → The guide says so. A host that needs an exact start sets the saga grain's
  interval to zero for the deployment that adds the saga, or seeds it at the head.
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
