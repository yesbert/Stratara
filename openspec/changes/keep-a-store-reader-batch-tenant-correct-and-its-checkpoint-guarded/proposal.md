# Keep a store reader batch tenant-correct and its checkpoint guarded

> **Status:** proposed

## Why

The round-5 audit of the Orleans execution model left four findings on the store readers open after
#107 and #109 — `R5-Rdr-004`, `R5-Rdr-008`, `R5-Rdr-010` and `R5-Tim-012`. None breaks a host on the
framework's defaults, which is why the integration suite is green; each breaks a host that has done
something the framework invites it to do.

- **A batch of up to 500 entries is applied from one scope whose services were resolved before any
  entry's session was set.** The projection grain resolves the projection handler and the projection,
  the saga grain resolves the saga manager and the processes, and only then sets the session recorded
  with each entry. On the bus path one bundle is one transaction, one session and one scope, and the
  session is set before the projection manager is resolved. A consumer that routes its database
  connection per tenant — the documented reason to replace the connection resolver — or whose
  read-model context takes the tenant when it is constructed therefore applies the whole batch under
  the first entry's tenant, or under none. A process grain reads the fact it was handed from the store
  before it sets the session, through the same tenant-routed context. Nothing in the documentation
  says that a batch is multi-tenant.
- **A checkpoint write is an unconditional update.** The shipped checkpoint store replaces the
  position and the reader's name of the row whatever they were. An activation that outlived its
  successor — a silo suspected dead that is still writing — rewinds the checkpoint the successor
  advanced, and a write under another reader's name is accepted where a read under it is refused.
- **Lowering the partition count under the native reader leaves the readers of the partitions that no
  longer exist alive.** Their keep-alive reminders bring them back every period, for ever; each one
  activates, is refused its checkpoint under the new count, logs an error and counts a stall.
- **A resumption the drain skips because a replay is active is silent.** A recorded command stays
  where it is for the length of the replay and nothing says why.

## What Changes

- **The session recorded with an entry is in place before anything that applies the entry is
  resolved.** A store reader applies a batch in runs — consecutive entries recorded under one session,
  the store's view of what one bundle was on the bus — and opens one scope per run, sets the session,
  then resolves the projection or the sagas. The process grain receives the recorded session with the
  fact it is handed and sets it before it reads the fact back.
- **A checkpoint advances only from the position its writer last saw, and never under another
  reader's name.** The checkpoint port gains an advancing member with a default, so a consumer's own
  store keeps compiling; the shipped store refuses a write that finds another position or another
  reader with a message naming both, and a reader that is refused re-reads the checkpoint the store
  holds instead of trusting its own.
- **A store reader activated for a partition the host no longer has retires itself:** it unregisters
  its keep-alive, logs that it did, and deactivates.
- **A resumption held back by a replay is logged** once when the drain starts holding it back and once
  when it resumes, on the drain and on the dispatcher alike; with the retired reader, three new log events under the Orleans band.
- **Consumer-visible effects:** a projection or saga whose services take their tenant when constructed
  now sees each entry's tenant; a checkpoint write from a stale activation fails where it silently
  rewound; a reader of a retired partition stops coming back; a held-back resumption is visible. One
  new member with a default on a public port and three log event ids. No schema change. Versioning:
  patch.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`:
  - *Projections and sagas read the store in commit order and never miss a committed fact* — the
    session is in place before the services that apply an entry are resolved, and a reader of a
    partition beyond the host's count retires; new scenarios.
  - *A read store's checkpoints belong to the consumers that read into it* — a checkpoint advances
    only from the position its writer last saw and never under another reader's name; new scenarios.
  - *An accepted command is recorded before the call returns and resumed after a crash* — a
    resumption held back by a replay is logged; new scenario.
- `projections`: *A projection can read the store from a checkpoint instead of consuming the bus* —
  the projection and what it depends on are resolved under the entry's session; new scenario.
- `sagas`:
  - *Sagas consume the event stream through their own subscription* — the sagas and what they depend
    on are resolved under the entry's session; new scenario.
  - *A saga can be a stateful process with a correlation and a timeout* — a fact reaches a process
    under its recorded session before the process's state is read; a timeout runs under the session
    the state stream was created with, which is read first, and the documentation says so; new
    scenario.

## Impact

- `Stratara.Abstractions` — `IProjectionCheckpointStore` gains `AdvanceAsync` with a default
  implementation.
- `Stratara.Orleans` — `ProjectionGrain`, `SagaGrain` (scope per session run), `SagaProcessGrain` and
  `ISagaProcessGrain` (the session travels with the fact), `StoreReaderGrain` and `StoreReaderSettings`
  (retirement beyond the partition count), `StoreReaderLoop` (advance instead of set; re-read on
  refusal), `OutboxDrainWork` and `OrleansCommandDispatcher` (the held-back resumption), a replay
  suspension tracker beside them, `OrleansLog`, `OrleansProjectionServiceCollectionExtensions` /
  `OrleansSingletonWorkServiceCollectionExtensions` (the tracker's registration).
- `Stratara.Orleans.EntityFrameworkCore` — `ProjectionCheckpointStore` (the guarded advance, the
  reader refusal on write).
- `Stratara.Diagnostics` — three log event ids in `LogEvents.Orleans` at the next free positions of the band (`117_114`–`117_116` as of #109; renumbered at apply if another change takes them first).
- `docs/guides/operate-the-orleans-execution-model.md` (the retired reader, the held-back resumption,
  the new event ids), `docs/guides/migrate-to-the-orleans-execution-model.md` (a batch is
  multi-tenant; the process timeout's session), `docs/guides/write-a-saga.md` (the timeout's session),
  `docs/reference/log-events-schema.md`, `CHANGELOG.md`, `llms.txt`.
- Tests: a batch of two tenants against a projection whose service captures the tenant at
  construction, on the projection and the saga grain; a process fact under a tenant-routed context;
  a stale activation's checkpoint write refused and the successor unharmed; a write under another
  reader refused; a reader beyond the count retiring; the held-back resumption logged once.
- Versioning: patch.
