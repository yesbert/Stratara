# Design — Optimise the Orleans execution model

## Context

See `proposal.md` → *Why* for the motivation. This section records where the proof of concept
stands, read against the source on 2026-09-14 at `20efaa3`, and which of its measured costs each
line of code is responsible for. The archived evidence is
`openspec/changes/archive/2026-09-13-prove-an-orleans-execution-model/evidence/results.md`; the
numbers below are its numbers.

### What the source does today, per cost

**B4 — a rebuild of one projection takes 94 % of a full replay (75.4 s against 80.5 s).**

- `ProjectionRebuilder.RebuildAsync` (`src/Stratara.Orleans/Projections/ProjectionRebuilder.cs`)
  pauses every partition's grain, truncates, resets the checkpoints, and then resumes the grains in a
  `foreach` that awaits each `ResumeAsync`. `ProjectionGrain.ResumeAsync` returns `CatchUpAsync()`,
  which reads and applies until the store has nothing newer. **The sixteen partitions are therefore
  rebuilt one after another**, and the grain path's one structural advantage over the replay —
  sixteen independent readers — is never used. The replay worker is single-threaded by design
  (`ProjectionReplayWorker.ReplayBatchAsync`), so 94 % is what one serial reader costs against
  another.
- `ProjectionGrain.ApplyEntryAsync` creates a service scope **per entry**, resolves every registered
  projection (`GetServices<IProjection>()` instantiates all of them) to find its own by name, computes
  the projection's relevant event-type names (a fresh array per call) and maps one entry at a time.
  `StoreReaderLoop.ApplyAsync` calls it once per entry under the preceding-fact retry pipeline.
- `PostgresTransactionIdReader.ReadAfterAsync` filters a partition with `bucket_id % 16 = p` over an
  index on `commit_transaction_id` alone, so the planner walks roughly sixteen index entries for every
  row it returns. This is a second-order cost; it is measured after the first-order one is gone.

**B5 — the silo host spends 4.04 CPU-seconds per 1 000 commands against 2.54 on the bus host.**

The probe command names a fresh aggregate every time (`LoadGenerator` in
`tests/Stratara.Orleans.IntegrationTests/Hosting/Scenarios/IntentScenario.cs`). Per command the
durable-intent path pays, beyond the handler both hosts run:

1. `OrleansCommandDispatcher.RecordAsync` — a write context, an outbox insert, a save;
2. a grain activation — the aggregate has never been seen — with a registration in the Redis grain
   directory, which the proof of concept made the default for every grain type
   (`PocSilo.Configure` → `UseRedisGrainDirectoryAsDefault`);
3. the grain call, whose argument `AggregateCommandEnvelope` is deep-copied because nothing marks
   it immutable;
4. `CommandExecution.CompleteIntentAsync` — a second write context and a delete, one round trip,
   awaited inside the grain's turn;
5. idle work the bus host does not have: the intent scenario sets the outbox drain to one second
   and the silo to a reminder refresh of five seconds and a minimum reminder period of one second —
   the profile the kill-and-restart tests need, not the one a deployed silo runs. Idle processor
   time was 0.056 CPU-s per second against 0.008 on the bus host, which alone is about seven per
   cent of the measured difference over the sixty-second load window.

**B3 — one aggregate under load runs at 87 % of the bus worker on the durable-intent shape (284
against 326 commands per second) and 99 % on the synchronous one.**

Everything is serial on one aggregate, so the number is a latency chain: the record (1), the grain
hop, the handler's own round trips, and the completion delete (4) — the last one paid *inside* the
turn, so the next command waits for it. The synchronous shape has no (1) and no (4) and lands at
parity; the difference between the two shapes is the cost of (1) and (4).

**B2 — the hint path is 0.2 ms behind the push at p50 and p99 12–17 ms against 9–11 ms.**

Every nudge is a full grain turn: `ProjectionGrain.NudgeAsync` calls `CatchUpAsync`, which reads
the checkpoint from the store (`StoreReaderLoop.CatchUpAsync` → `checkpoints.GetAsync`), reads a
batch, applies, and writes the checkpoint with a read-then-save (`ProjectionCheckpointStore.SetAsync`:
`SingleOrDefaultAsync`, then `SaveChangesAsync`). Nudges that arrive while a catch-up runs queue as
further turns and each repeats the checkpoint read and the store read, usually to find nothing. At
50 events per second over sixteen partitions that is the p99.

**Restart on the same endpoint waits one to two minutes.** T5 took 23 minutes for ten kills. The
membership protocol ignores a stale entry only after `NumMissedTableIAmAliveLimit` (3) periods of
`IAmAliveTablePublishTimeout` without an `IAmAlive` write (Microsoft Learn, *Cluster management in
Orleans*, read 2026-09-14). The proof of concept runs the defaults.

### Constraints carried over

The hard constraints of the archived design hold unchanged: additive only, no silent change of
semantics, database neutrality through ports, the event stream as the only truth, every `projections`
and `sagas` guarantee kept on the Orleans path, storage-backed single activation where it matters,
clean shutdown and one reset. The known limitations listed in that design (no diagnostics, permits
without a lease, unbounded resumed intents, simple-name identities, hosted-service starters,
cancellation not reaching the store) are **not** addressed here unless an optimisation touches the
line; they remain productisation items.

## Goals / Non-Goals

**Goals:**

- Remove the costs above that are ours, and measure each removal against the archived number with
  the bus path as the in-run control.
- Keep every correctness test of the archived change green, unchanged in what it asserts.
- Leave the design of the execution model as it is: the same grains, the same ports, the same
  registrations a consumer would call.

**Non-Goals:**

- Anything a consumer can observe: no packable project, no composite change, no store schema change
  outside the proof-of-concept model extension, no spec delta.
- Optimising the bus path or the shipped store. The bus numbers are the control and are not touched.
- Revisiting the recommendation. `results.md` says what the new numbers are; whether they change the
  recommendation is a decision the owner takes with the archived evidence beside the new one.

## Decisions

### D1 — The rebuild resumes every partition at once, and resuming does not run the catch-up

`ProjectionRebuilder` resumes the partitions with one `Task.WhenAll`. `ProjectionGrain.ResumeAsync`
clears the pause and *requests* a catch-up through the same path a nudge takes (D3); it returns when
the request is recorded, not when the store is drained. The rebuild benchmark already waits for the
views, not for `RebuildAsync`, so its measurement is unaffected by the earlier return.

*Rejected: keeping `ResumeAsync` synchronous and only parallelising the rebuilder.* Sixteen
concurrent awaits on sixteen grains would work, but a caller that resumes one grain would still
block for that partition's whole catch-up, which is not what "resume" means; and the resume would be
the one place where a catch-up runs inside a caller's request instead of the grain's own schedule.

Evidence: `ProjectionRebuilder.cs` lines 45–50 and 70–76; `ProjectionGrain.cs` `ResumeAsync`;
`RebuildRun.PerProjectionRebuildAsync` waits on `WaitForViewsAsync`.

### D2 — A batch is applied under one scope

`StoreReaderLoop.CatchUpAsync` hands the loop's caller a whole batch and expects back the index of
the first entry that did not apply. `ProjectionGrain` creates one scope per batch, resolves the
projection handler and its own projection once, holds the relevant event-type names in a set built
once per activation, and then, per entry: sets the recorded session, maps the entry, filters, and
applies under the preceding-fact retry pipeline exactly as before. The per-entry semantics that the
`projections` guarantees rest on — the recorded session of *that* entry, the retry of a missing
prerequisite, the stop at a genuine failure, the checkpoint that moves only past what applied — are
unchanged because the unit of apply and of retry is still the entry; only the scope and the lookups
are hoisted.

*Rejected: one `ProjectAsync` call for the whole batch.* It would lose the index of the first failure
and make the checkpoint resume from the batch start, which is correct under idempotent apply but
re-applies a whole batch for one missing prerequisite. Not worth it for what it saves.

*Deferred: the partition index.* An index that serves `bucket_id % n = p` needs either an expression
index, which EF Core's model API does not express, or a stored generated column with the partition
count baked into the schema. It is built and measured only if D1 and D2 leave the rebuild above its
expectation, because the reader's extra index walks are second-order next to a serial rebuild.

Evidence: `StoreReaderLoop.cs` `ApplyAsync`; `ProjectionGrain.cs` `ApplyEntryAsync`;
`ProjectionHandler.ProjectAsync` iterates events one by one, so per-entry calls cost nothing extra.

### D3 — A nudge marks the grain dirty; one catch-up loop drains until clean

`IProjectionGrain.NudgeAsync` (and the saga grain's) becomes `[OneWay]` and `[AlwaysInterleave]`: it
runs even while a catch-up is in flight, sets a dirty flag, and starts the loop only if none runs.
The loop — the one thing that reads the store — runs as a single logical catch-up: `while dirty: clear
dirty, read and apply until the store is empty`. A nudge that lands mid-loop costs one flag write and
at most one extra read at the end; before, it cost a queued turn with a checkpoint read and a store
read. The poll timer and the keep-alive reminder set the flag too, so the safety net still reads
even when nothing was nudged. The explicit `CatchUpAsync` the tests and the rebuild use awaits the
running loop and returns what it applied.

The turn no longer serialises the batch; the loop's single-flight guard does. That is the same
guarantee — one batch in flight per grain — kept by a field instead of the scheduler, and it is
what allows the interleaved nudge.

*Rejected: `[Reentrant]` on the grain.* It would let every method interleave, including the pause
and the explicit catch-up, and the "no batch in flight when `PauseAsync` returns" promise the
rebuilder relies on would need its own guard anyway.

*Rejected: a timestamp on the nudge to skip stale ones.* Clocks across silos would decide whether a
read happens; the flag needs no clock.

Evidence: `ProjectionGrain.cs` `NudgeAsync`, `CatchUpAsync`, timer registration with `Interleave =
false`; Microsoft Learn, *Reentrancy* and `AlwaysInterleaveAttribute`, read 2026-09-14.

### D4 — The grain keeps its position; the store is read on activation and written with one upsert

`StoreReaderLoop` holds the position it last wrote and reads the checkpoint store only when it has
none — on activation, and after a pause, because a pause is what the rebuilder does before it resets
a checkpoint behind the grain's back. `ProjectionCheckpointStore.SetAsync` becomes one
`INSERT … ON CONFLICT DO UPDATE` instead of a query and a save. `IProjectionCheckpointStore` keeps its
shape; the reader-name check on `GetAsync` stays.

Consequence for the tests: `ProjectionGrainTests` resets a checkpoint in the store and expects the
next `CatchUpAsync` to re-apply from it. With a cached position that expectation holds only through
pause and resume, which is the sanctioned way to change a checkpoint under a running grain and what
the rebuilder does. The test is changed to pause, reset, resume; what it asserts — idempotent
re-apply from a reset checkpoint — is unchanged.

*Rejected: reading the checkpoint on every catch-up and only dropping the queued turns (D3).* The
read is one round trip per nudge on the latency path; D3 removes the queued ones and this removes
the rest.

Evidence: `ProjectionCheckpoint.cs` `SetAsync`; `ProjectionGrainTests.cs` first test, the loop that
sets the checkpoint to 0 and calls `CatchUpAsync`.

### D5 — Intents are completed in batches, outside the turn

`CommandExecution.RunAsync` hands a completed intent's id to a per-silo completion queue instead of
deleting it, and the turn ends. The queue flushes as one `DELETE … WHERE id = ANY(…)` when it holds a
bounded number of ids or a short window has elapsed, whichever comes first, on one write context
per flush; on host stop it flushes what it holds. An intent whose host dies between the handler's
completion and the flush is resumed by the drain after `IntentGrace` and its handler runs a second
time — which is exactly the case the durable-intent shape already allows for a host that dies
between the hand-off and the completion, one window later. The window is far below `IntentGrace`
and the design says so in the option's documentation.

*Rejected: fire-and-forget of the single delete.* It removes the round trip from the turn but keeps
one context and one statement per command, which is the processor time B5 measures.

*Rejected: deleting the intent inside the handler's own transaction.* The handler may not open one
(the probe handler writes with raw SQL), and the framework's unit of work is the handler's, not the
dispatcher's.

Evidence: `AggregateGrain.cs` `CompleteIntentAsync`; `OrleansCommandDispatcher.cs` `RecordAsync`;
`archive/…/design.md` D5 and *Known limitations* → resumed intents.

### D6 — The envelope is immutable

`AggregateCommandEnvelope` carries three strings and is never modified after construction; it is
marked `[Immutable]` so a local call passes it by reference. Free, and correct by construction.

Evidence: Microsoft Learn, *Serialization of immutable types in Orleans*, read 2026-09-14.

### D7 — The grain directory is measured per grain type, not decided

The archived design chose a storage-backed directory (D7 there) so single activation does not
depend on a calm cluster — a property the projection, saga, singleton and timer grains need. An
aggregate grain is short-lived, is activated once per aggregate that is touched, and has the store's
unique version constraint behind it: a duplicate activation under an unstable cluster ends in a
concurrency conflict, which is what the bus path has always had across processes. The silo profile
therefore gets a switch — Redis as the default directory for every grain (as today) or Redis as a
named directory the long-lived grains select with `[GrainDirectory]` while aggregate and runner
grains use the built-in one — and B3 and B5 run both. Which one a shipped package would default to
is recorded in `results.md` with the number; it is not decided here.

Evidence: `PocSilo.Configure` → `UseRedisGrainDirectoryAsDefault`; Microsoft Learn, *Grain
directory* (built-in directory recommended as the starting point; storage-backed for grains that
need a stronger single-activation guarantee), read 2026-09-14.

### D8 — The benchmark silo runs a production-shaped profile

`PocSilo.Configure` takes a profile. `Test` is what it configures today: a one-second minimum
reminder period, a five-second reminder refresh, and the scenarios' one-second drain. `Production`
leaves `ReminderOptions` and `OutboxDrainOptions` at their defaults. The kill-and-restart tests keep
`Test`; every benchmark run uses `Production`, because the bus host in the same run pays its own
production defaults and the comparison is otherwise not of like with like. `results.md` names the
profile of every number.

Evidence: `PocSilo.cs` constants; `IntentScenario.BuildAsync` option values; B5 idle processor time
in `raw/resources/20260913-155549/result.json`.

### D9 — The restart delay is measured under two membership settings, in a run of its own

A new benchmark run starts the intent host as a separate process, kills it, restarts it on the same
endpoint and measures the time from the restart to the first successful grain call — under the
default `ClusterMembershipOptions`, and under a profile with `IAmAliveTablePublishTimeout` and
`NumMissedTableIAmAliveLimit` lowered so a stale entry is ignored within the expectation. The
hard-kill timer test then runs under the shorter profile once to show it declares nothing falsely
dead. The setting a shipped package would recommend is recorded with the number.

*Rejected: shortening probe timeouts.* In a single-silo restart there is nobody left to probe; the
stale entry is skipped through the `IAmAlive` timestamp, and that is the setting that decides.

*Found while measuring (2026-09-14):* the archived reading was mislocated. A silo restarted on the
**same** endpoint joins in under a second — Orleans 10.3.1 marks the older clone of itself dead on
start ("Detected older version of myself") — and a reminder registered before the kill fires on its
due time. What waits is a silo joining on **another** endpoint while a killed silo's entry is still
Active: `MembershipAgent.ValidateInitialConnectivity` has to reach that silo and gives up on the
entry only once it is stale (`NumMissedTableIAmAliveLimit` × `IAmAliveTablePublishTimeout`, 90 s by
default). The integration suite hits this because every test class starts silos on its own ports
against one shared membership table after earlier classes killed theirs; the archived kill tests
paid it once per host, which is where their minutes went. The test cluster therefore runs the
shortened setting by default, the benchmark silos keep Orleans' defaults, and R1 measures both
shapes. `ValidateInitialConnectivity` is not a public option in 10.3.1, so the shortened `IAmAlive`
settings are the lever.

Evidence: Microsoft Learn, *Cluster management in Orleans* → membership protocol configuration and
*IAmAlive writes*, read 2026-09-14; T5's duration in the archived `results.md`.

### D10 — Evidence lives in the change, expectations before runs

As in the archived change: `evidence/expectations.md` is committed before the first run; raw output
goes to `evidence/raw/<measurement>/<timestamp>/` with commit, hardware, images and package
versions; `evidence/results.md` compares each number with the archived one and names the profile
and the directory setting it was measured under. A threshold written after a run is not a threshold.

## Risks / Trade-offs

- **[The interleaved nudge weakens the turn as the serialisation]** → The single-flight guard is
  the serialisation for the loop; `PauseAsync` awaits a running loop before it returns, so the
  rebuilder's "no batch in flight" promise holds. `ProjectionGrainTests` and the saga grain tests
  are the check.
- **[A cached position drifts from the store]** → Only a pause invalidates it, and only the
  rebuilder and the tests write checkpoints behind the grain; both pause first. A second activation
  of the same grain (directory instability) reads the store on activation, applies idempotently
  from there, and the worst case is a re-applied batch, as before.
- **[Batched completion widens the double-run window]** → By one flush window, bounded and
  documented; below `IntentGrace` by two orders of magnitude. `DurableIntentTests` still kills
  between hand-off and completion and must still resume.
- **[The built-in directory for aggregate grains allows a duplicate activation under churn]** →
  Measured, not chosen; the unique version constraint is the backstop on both paths, and the
  archived T3 still holds because ordering rests on the send lane, not on the activation.
- **[Shorter `IAmAlive` settings cost table writes]** → A write every few seconds per silo is
  measured in the restart run's idle samples; the profile is a recommendation with its cost.
- **[Benchmarks on a different day]** → Each run carries the bus path as its own control; the
  comparison in `results.md` is grain-to-bus within a run, and the archived grain-to-bus ratio is
  the baseline, not the archived absolute number.
- **[Scope creep into productisation]** → The known limitations stay listed and untouched; a task
  that would fix one is out of scope unless the optimisation cannot be done without it.

## Migration Plan

Nothing is deployed and nothing is published. Reverting the branch undoes everything; the
proof-of-concept model extension's index, if built, is created by `EnsureCreated` on test databases
only.

## Open Questions

None that change the tasks. Which directory and which membership profile a shipped package would
default to are answered by D7's and D9's numbers and recorded in `results.md` for the change that
ships the model.
