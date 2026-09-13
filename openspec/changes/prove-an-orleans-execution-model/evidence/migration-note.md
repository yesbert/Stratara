# Migration note — from the bus workers to the Orleans execution model

> **Status:** draft, written against the proof of concept on 2026-09-13. It describes what the
> proof of concept does; a shipped package may name things differently. It names no consumer.

For a host that runs Stratara 4.0.x with the bus workers and wants to move to the Orleans execution
model, block by block. Handlers, projections and sagas do not change. What changes is registration,
what gets stronger, and what a handler no longer needs to worry about but may keep doing.

## What a host adds once

| | |
|---|---|
| A silo | `builder.UseOrleans(silo => …)` with a **storage-backed clustering** (ADO.NET on the store's database in the proof of concept), a **reminder service** on the same store, and the **Redis grain directory** as the default directory. Localhost clustering is for development only. |
| The Orleans SQL scripts | The ADO.NET providers ship no scripts. The three PostgreSQL scripts (main, clustering, reminders) from the Orleans repository at the pinned tag are applied once, in that order. |
| A store schema addition | The commit-order reader's columns and the partition counter table (`CommitOrderModel`) on the write context, and the checkpoint table (`ProjectionCheckpointModel`) on the read context. Both are migrations; the proof of concept creates them with `EnsureCreated`. |
| A reader | One `ICommittedPositionReader`, scoped: `PostgresTransactionIdReader<TWriteContext>` on PostgreSQL, `PortableCounterReader<TWriteContext>` anywhere else (with the counter switched on). |
| A reminder period | Sub-minute timers need `ReminderOptions.MinimumReminderPeriod` lowered on the silo; Orleans warns and allows it. |

## What changes per role

The composites a host calls today stay. Each role adds one call after its composite.

| Role today | Add after the composite | What the call does |
|---|---|---|
| Command worker (`AddCommandWorkerServices`) | `AddStrataraAggregateGrains()` | Commands that name an aggregate run in that aggregate's grain; the bus consumer keeps consuming what still arrives over the bus, so the two can coexist during a rollout. |
| Backend / API (`AddBackendServices`) | `AddStrataraOrleansCommandDispatcher()` + `AddStrataraSingletonWork<OutboxDrainWork>()` | `ICommandOutboxDispatcher` records the command in the outbox and hands it to its grain instead of publishing; the singleton drain resumes any hand-off a crash lost. The outbox table is reused as the intent record — no new table. |
| Projection worker (`AddEventProjectionWorkerServices`) | `AddStrataraProjectionGrains<TReadContext>()` | One grain per projection and partition reads the store in commit order from a checkpoint; the bundle dispatcher becomes a wake-up hint. The bus projection worker and the full-replay worker are removed from the host by this call. |
| Saga worker (`AddSagaWorkerServices`) | `AddStrataraSagaGrains<TReadContext>()` | One grain per partition hands each fact to the saga manager; the bus saga worker is removed. Stateful processes opt in by deriving from `SagaProcess<TState>`. |
| Outbox worker (`AddOutboxWorkerServices`) | `AddStrataraSingletonWork<OutboxDrainWork>()` on any silo, and drop the outbox worker host | The drain runs once per cluster in a grain; the Redis lock and the "deploy one instance" rule go. |
| Heavy command worker (`AddHeavyCommandWorkerServices`) | nothing; heavy commands go to the bounded worker pool through the dispatcher | `ConfigureStrataraHeavyWork(o => o.ClusterWideLimit = …)` sets the cluster-wide bound. The heavy lane topic and its host go. |
| Timeouts and wake-ups a host implemented itself | `AddStrataraDurableTimers()` + an `ITimerOwners` and an `ITimerHandler` | Owner-checked one-shot timers in the reminder table. |

Two things a host must do that the bus never asked for:

- **Development environment or a real key store.** Nothing new — the proof of concept ran in Development with the dummy key store, as the existing test support does.
- **A rebuildable projection** implements `IRebuildableProjection.TruncateAsync` to be rebuilt alone; a projection that does not is rebuilt with the others by the framework's replay, as today.

## Guarantees that get stronger

| Guarantee | With the bus workers | With the grains |
|---|---|---|
| One writer per aggregate | per worker process; two processes can conflict and requeue | one activation per aggregate id across the cluster, backed by the storage grain directory |
| A committed fact reaches every projection and saga | only if the publish after the commit succeeded (SF-002); otherwise a full replay | always, from the store, in commit order, from a checkpoint — a crash costs latency, not a fact (T1) |
| A failed projection or saga | the bundle is discarded on RabbitMQ (SF-001); repaired by a full replay | the checkpoint stops at the failing entry and retries; nothing is discarded — and nothing after it in that partition advances until it passes, which is the trade-off to know about |
| Rebuilding a read model | truncates every read model and replays every event | one projection at a time; the others keep applying live events |
| Singleton work | a Redis lock or a deployment assumption | one grain for the name |
| Timeouts | left to the host | durable, owner-checked, once per cluster |
| Command order for one aggregate from one caller | none — two consumers may reorder | preserved within a scope; the framework serialises the sends, because Orleans promises no order either |

## Handler assumptions that become unnecessary but stay harmless

- **Retrying on `ConcurrencyException`.** Two commands on one aggregate no longer run concurrently anywhere; a handler that still retries just never has to.
- **Expecting a duplicate delivery.** Still possible: a handler that ran past its append but died before the intent was completed runs again after the drain resumes it; a projection is re-applied after a checkpoint that was not yet written. Idempotency stays required, as it always was under at-least-once.
- **Reporting a missing prerequisite.** Still meaningful across partitions — a fact from another stream may not be applied yet — and now retried by the grain without advancing the checkpoint.
- **Assuming a full replay truncates everything.** A rebuild of one projection touches only that projection.

## What does not change

`ISaga`, `IProjection`, `ICommandHandler<T>`, `IAggregateScopedCommand`, `IHeavyCommand`,
`IEventSource`, `ICommandOutboxDispatcher`'s promise, the outbox table, the event stream. A host can
run both models at once during a rollout: the bus consumer and the grain path both apply
idempotently.
