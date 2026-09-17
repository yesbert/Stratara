## Context

See `proposal.md` — Why. The current code:

- **Batch scope.** `ProjectionGrain.ApplyBatchAsync` (`src/Stratara.Orleans/Projections/ProjectionGrain.cs:134-155`)
  opens one scope for the batch, resolves `ISessionContextProvider`, `IProjectionHandler` and the
  projection (lines 136-140), and sets the recorded session per entry afterwards (line 145).
  `SagaGrain.ApplyBatchAsync` (`src/Stratara.Orleans/Sagas/SagaGrain.cs:71-94`) does the same with
  `ISagaManager` and the processes (lines 73-77, session at 81). `SagaProcessGrain.HandleAsync`
  (`src/Stratara.Orleans/Sagas/SagaProcessGrain.cs:44-53`) resolves the process, checks the state
  stream's existence and loads the state in `RunAsync` (lines 83-89) and reads the fact back through
  `IWriteUnitOfWork` (lines 137-144) before it sets the session (line 47). The bus path,
  `ProjectionWorker.ApplyAsync` (`src/Stratara.Projections/Services/ProjectionWorker.cs:170-182`),
  opens one scope per bundle, sets the bundle's session first, then resolves `IProjectionManager`.
- **What captures the tenant.** `ISessionContextProvider` is scoped (`SessionServiceCollectionExtensions.cs:34`)
  and holds a plain property (`SessionContextProvider.cs:10`), so what a scope's services read at
  construction is whatever was set in that scope before. The framework's own write-context factory
  resolves the connection string through `IDbResolver` when a context is created
  (`NpgsqlDbContextServiceCollectionExtensions.cs:131-136`, `ResolveTenantConnectionString`), and the
  resolver is documented as the extension point for per-tenant routing (`DefaultDbResolver.cs:7-17`).
  A store-reading grain's batch therefore takes the connection of the tenant that was ambient when its
  first context was created — none, today.
- **Batch shape.** A `CommittedBatch` holds up to `BatchSize` (500) `EventStreamEntry` rows of one
  partition; one row is one event with its recorded session (`RecordedSession.Of`,
  `ProjectionGrain.cs:182-191`), and `SessionContext` is a record (`Stratara.Contracts/Session/SessionContext.cs:26`),
  so two entries of one transaction carry equal sessions.
- **Checkpoint write.** `ProjectionCheckpointStore.SetAsync` (`src/Stratara.Orleans.EntityFrameworkCore/Projections/ProjectionCheckpointStore.cs:58-89`)
  is `UPDATE … SET position, reader WHERE projection AND partition`, then an insert if no row. `GetAsync`
  (lines 13-29) refuses another reader's name; the write does not. The loop writes through the port's
  `SetAsync` after every batch and after a cut batch (`StoreReaderLoop.cs:217,225`); the rebuild, the
  replay reset and the seeding write `0` or the head through the same member
  (`ProjectionRebuilder.cs:53`, `ReplayCheckpointReset.cs:43`, `StoreReaderSeeding.cs:43`). The loop
  caches the position it last wrote and reads the store only when it has none (`StoreReaderLoop.cs:186-190`).
  `IProjectionCheckpointStore` is public in `Stratara.Abstractions`.
- **Keep-alive.** `StoreReaderGrain.EnsureRunningAsync` registers a reminder named `keep-alive` per
  grain (`StoreReaderGrain.cs:73`); `ReceiveReminder` requests a catch-up (line 89). Nothing unregisters
  it. `StoreReaderGrainStarter` (`ProjectionGrain.cs:247-266`) calls `EnsureRunningAsync` for partitions
  `0 … PartitionCount-1` only. A grain of a partition ≥ the count reads a checkpoint written under the
  reader name `…/<old count>` and is refused (`ProjectionCheckpointStore.Refusal`), which
  `CatchUpAsync` logs as `117_103` and counts as a stall (`StoreReaderLoop.cs:166-170`).
  `ExecutionModelReset` deletes the service's reminders while no silo runs.
- **Held-back resumption.** `OutboxDrainWork.ResumeRecordedCommandsAsync` (`src/Stratara.Orleans/Singleton/OutboxDrainWork.cs:74-77`)
  returns without resuming when a replay is active, silently; `OrleansCommandDispatcher.ResumeDueAsync`
  (`src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs:62`) returns `0` in the same case.
  Singleton work is registered scoped (`OrleansSingletonWorkServiceCollectionExtensions.cs:39`) and
  resolved per run (`SingletonWorkGrain.cs:87-91`), so an instance field cannot remember a transition.
  Log ids end at `117_113` (`src/Stratara.Diagnostics/LogEvents.cs:346`).

## Goals / Non-Goals

**Goals:**
- A projection, a saga or a process whose dependencies take the tenant or user at construction is
  applied under each entry's own session on the store path, as it is on the bus path.
- A checkpoint never rewinds, never overtakes and never changes reader by a write the framework makes.
- Lowering the partition count under the native reader leaves no reader that returns for ever.
- A resumption held back by a replay is visible in the log, once per transition.

**Non-Goals:**
- A scope per entry. The bus path's unit is a bundle — one transaction, one session; the store path's
  unit becomes the same thing.
- Carrying the session with a durable timer. The timeout path of a process reads its state stream
  before any session can be in place; carrying the session in the timer table is a schema change and
  a change to the timer port, and is documented as a limit instead (D2).
- A version column on the checkpoint row, or a change to the checkpoint schema.
- Renumbering or migrating checkpoints when the partition count changes; that stays a reset.

## Decisions

### D1 — One scope per session run, the session set before anything is resolved

`ApplyBatchAsync` in the projection and the saga grain walks the batch in runs: consecutive entries
whose `RecordedSession.Of(entry)` are equal. For each run it opens a scope, sets the session, resolves
the handler and the projection (or the saga manager and the processes), and applies the run's entries
one by one through `Loop.ApplyEachAsync`'s per-entry retry, setting the session again per entry so a
correlation that differs within a run is still the entry's. The `_relevant` set and the cached
projection type stay per grain. Entries of one transaction share a session, so a run is the bundle
the bus path would have applied from one scope; a store whose commands each carry their own
correlation gets one scope per transaction, which is what the bus path costs today, and never more
than one scope per entry.

`ApplyEachAsync` keeps its contract — the index of the first entry that did not apply — by taking the
batch and a callback that opens the scope at a run boundary; the loop remains the only place that
retries, logs and counts.

*Rejected: one scope per batch with the session set from the first entry before resolution.* Applies
five hundred entries under one tenant; wrong for every entry but the first run.
*Rejected: one scope per entry.* A context per event where the bus path had one per transaction; the
throughput the grains were measured at (`optimise-the-orleans-execution-model`) assumed fewer.
*Rejected: runs keyed by the tenant only.* A user-scoped dependency is as legitimate as a
tenant-scoped one, and the session is what the bus path sets; equality of the record is the cheapest
correct key.

Evidence: `ProjectionGrain.cs:134-155`, `SagaGrain.cs:71-94`, `ProjectionWorker.cs:170-182`,
`SessionContextProvider.cs`, `NpgsqlDbContextServiceCollectionExtensions.cs:131-136`. Test: a probe
projection and a probe saga depending on a scoped service that captures `ISessionContextProvider.Current?.TenantId`
in its constructor; two tenants' entries in one partition and one read; assert the captured tenant per
entry equals the entry's — against today's code the second tenant's entries see the first's or none.

### D2 — The session travels with the fact to the process grain; the timeout's session is a documented limit

`ISagaProcessGrain` gains `HandleAsync(Guid streamId, long version, RecordedSessionCarrier session, CancellationToken)`
under a new alias, where the carrier is an Orleans-serialisable record beside the grain holding the
`SessionContext` fields; the saga grain passes `RecordedSession.Of(entry)`. The process grain sets the
carried session first, then resolves the process, reads the fact back (still the truth — the carried
session is a hint like the stream and version), sets the session from the entry, and continues as
today. The old method stays for one release so a saga grain of the previous version in a rolling
cluster still reaches a process grain of this one; it behaves as today.

`OnTimeoutAsync` is unchanged: it reads the state stream's first entry to find the session and sets it
before the process runs. That read happens under no session by construction — a timer carries an
owner and a purpose, not a tenant. The guide's process section and `write-a-saga.md` say so: a
process whose state stream is reachable only under its tenant's connection does not get its timeouts
through a tenant-routed resolver; the resolver has to answer for an absent tenant with a connection
that holds the state streams.

*Rejected: reading the fact under no session and setting it afterwards (today).* The read itself is
what a routed resolver cannot serve.
*Rejected: carrying the session in the timer registration.* `TimerRegistration` is a public record
(`IDurableTimers.cs:46`) and the timer table's shape is a consumer's migration; out of proportion for
this change, recorded as an open question.

Evidence: `SagaProcessGrain.cs:44-53,83-89,137-144`, `SagaGrain.cs:85-91`, `IDurableTimers.cs:46`.
Test: a process behind a resolver that throws for an absent tenant; a fact of a tenant handed to it
applies and appends; the timeout path is covered by the documentation test only.

### D3 — `AdvanceAsync` on the port with a default; the shipped store guards by position and reader

`IProjectionCheckpointStore` gains
`Task AdvanceAsync(string projection, int partition, string reader, long from, long to, CancellationToken)`
with a default implementation that calls `SetAsync`, so a consumer's own store keeps compiling and
keeps its current behaviour until it overrides. The shipped store implements it as one conditional
update — `WHERE projection AND partition AND position = from AND reader = reader` — and, when no
row changed, reads the row: absent and `from == 0` → insert as today, with the same key-race fallback
re-running the conditional update once; present → `InvalidOperationException` naming the position
found and the one expected, or both readers where the reader differs. `SetAsync` keeps replacing the
position — it is the reset verb the rebuild, the replay reset and the seeding use — but refuses a row
held under another reader with the same reader message; all three write under the reader the host
registered, and a host that switches readers is already told to reset first.

The loop calls `AdvanceAsync(consumer, partition, reader, _position, batch.Position)` after a batch and
`AdvanceAsync(…, _position, resumeAt)` after a cut batch. A refusal propagates as today — logged as
`117_103`, counted as a stall on the read — and the loop calls `Invalidate()` on the way out, so the
next catch-up reads the position the store holds and continues from there.

*Rejected: an expected-position parameter on `SetAsync`.* Breaks every implementer of a public port.
*Rejected: a row version.* A schema change for what the position already gives: a reader's positions
are monotone, so "the position I last wrote" is the version.
*Rejected: refusing on the loop side by re-reading before every write.* Two round trips per batch to
detect what one conditional update detects.

Evidence: `ProjectionCheckpointStore.cs:50-89` (the remark on the first-write race that the new member
keeps), `StoreReaderLoop.cs:186-190,212-227`, `ProjectionCheckpointStoreTests.cs` (the fixture the new
tests reuse). Test: two stores over one database as two activations; the first advances 0→10, the
second 10→20, the first 10→15 is refused naming 20 and 10, the row says 20; a `SetAsync` under another
reader refused naming both; `AdvanceAsync` on a fake port without an override reaches `SetAsync`.

### D4 — A reader beyond the partition count retires on activation

`StoreReaderSettings` gains the partition count, taken from `CommitOrderOptions` where the projection
and saga grains build their settings. `StoreReaderGrain.OnActivateAsync` compares its parsed partition
with it: at or above, the grain unregisters the keep-alive reminder if one exists, logs
`StoreReaderRetired` (information; the ids below are the next free ones in the band as of #109, `117_114`–`117_116`, and are renumbered at apply if another change lands first) with consumer, partition and count, registers no poll
timer and no loop, and calls `DeactivateOnIdle()`. Every method of a retired grain is a no-op —
`EnsureRunningAsync` and `NudgeAsync` return, `CatchUpAsync` returns 0, `PositionAsync` returns 0,
`PauseAsync` and `ResumeAsync` return — so a rebuild or a reset that still enumerates the old count
does not fail on it. The starter only ever asks for partitions below the count, so nothing registers
the reminder again.

*Rejected: unregistering from the starter.* It would have to know the old count to name the grains.
*Rejected: leaving it to the reset.* The reset runs while nothing runs; a team that lowers the count and
resets by the guide is already clean, and this decision is for the team that did not.

Evidence: `StoreReaderGrain.cs:54-73,89`, `ProjectionGrain.cs:247-266`, `ProjectionCheckpointStore.cs:35-42`.
Test: a cluster at four partitions with a reader's reminder registered for partition 3; restarted at two
partitions after a reset; the reminder tick activates the grain, which logs the retirement, unregisters
the reminder, and counts no stall.

### D5 — A held-back resumption is logged once per transition, from one tracker

An internal `ReplaySuspensionTracker`, registered singleton by `AddStrataraSingletonWork` and by the
command dispatcher's registration, holds one flag. `OutboxDrainWork.ResumeRecordedCommandsAsync` and
`OrleansCommandDispatcher.ResumeDueAsync` call `tracker.HeldBack(logger)` where they skip — it logs
`ResumeHeldBackByReplay` (`117_114`, information) on the first call and nothing after — and
`tracker.Released(logger)` on the run that resumes again, which logs `ResumeReleasedAfterReplay`
(`117_116`, information) only if a hold was logged. Both run on the drain's period; the tracker is the
only state between runs, because the work itself is scoped and resolved per run.

*Rejected: a line per skipped run.* A replay of an hour at the default period is 720 lines that say the
same thing.
*Rejected: a debug-level line.* The finding is that nothing says why a command waits; debug is off in
production.

Evidence: `OutboxDrainWork.cs:59-81`, `OrleansCommandDispatcher.cs:62`, `SingletonWorkGrain.cs:87-91`,
`OrleansSingletonWorkServiceCollectionExtensions.cs:39`, `RecordedCommandDrainTests.cs` (the logger
capture to reuse). Test: three runs under an active replay log one hold; the first run after it logs one
release; without a hold, nothing.

## Risks / Trade-offs

- [More scopes per batch on a multi-tenant store] → one per session run, at most one per entry; a
  single-tenant store with one command per correlation pays one per transaction, which is what the
  bus path paid.
- [A consumer's own checkpoint store does not override `AdvanceAsync`] → the default keeps today's
  behaviour; the member's remarks say what an override buys.
- [The first-write race under the guard] → both writers expect 0; the loser's conditional update
  finds the winner's position and is refused, then re-reads — the winner's position stands, which is
  what the old fallback silently overwrote.
- [A retired grain answers a rebuild's pause with a no-op] → the rebuild resets checkpoints for the
  current count only; the retired partition has none to reset.
- [The old `HandleAsync` alias lingers] → removed with the next major, listed in the retirement of
  deprecated members.

## Migration Plan

Patch release. New public surface: `IProjectionCheckpointStore.AdvanceAsync` with a default; three log
event ids. A consumer with its own checkpoint store may override the member. No schema change. No
action for a host on the defaults.

## Open Questions

- Should a durable timer carry the session it was registered under, so that a process timeout can be
  served through a tenant-routed resolver? It is a change to `TimerRegistration` and the timer table
  and is left for the owner; this change documents the limit.
