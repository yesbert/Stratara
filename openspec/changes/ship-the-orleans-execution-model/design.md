# Design — Ship the Orleans execution model

## Context

See `proposal.md` → *Why*. The code to ship exists: `src/Stratara.Orleans/` at `main` after #77
and #78, non-packable, with its integration suite (37 tests) and benchmarks. Two archived designs
carry the decisions it was built on — `2026-09-13-prove-an-orleans-execution-model` (D1–D10, the
readers, the send lane, the hybrid shape) and `2026-09-14-optimise-the-orleans-execution-model`
(D1–D10, the single-flight loop, the completion queue, the directory per grain type, the profiles).
This design does not restate them; it decides what turning the proof of concept into packages
changes, and closes the list the first design left open under *Known limitations of the
proof-of-concept code*, item by item:

| Known limitation (2026-09-13) | Closed by |
|---|---|
| No diagnostics | D5 |
| Heavy-work permits are a counter without a lease | D6 |
| A resumed intent has no bound | D4 |
| Reader and saga identities are simple type names | D7 |
| The grain starters are hosted services | D8 |
| Cancellation does not reach the store | D9 |

And the three items the pre-merge review of #77 named, recorded in the archived `results.md` under
*Still open*: batch deletion through the repository port (D3), the start-up check for the durable
directory (D2), and a batch that says whether more exists (D10).

Facts the design rests on, read 2026-09-14:

- `Directory.Build.props` → `<VersionPrefix>4.0.4</VersionPrefix>`, `IsPackable=false` by default,
  a packable csproj opts in and is listed in `Stratara.Publish.slnf` (25 projects today).
- `src/Stratara.Diagnostics/LogEvents.cs` allocates `100_000–116_999`; the next band is `117_000`.
- `src/Stratara.EventSourcing.EntityFrameworkCore` depends on the PostgreSQL provider directly
  (archived correction C7), so a reader native to PostgreSQL in the persistence package adds no
  dependency the package does not already have.
- `AddEventProjectionWorkerServices` and `AddSagaWorkerServices` register their bus-fed workers
  inside one call; the proof of concept removed them by implementation name.

## Goals / Non-Goals

**Goals:**

- Two packages a consumer installs from the feed, at the lockstep version, documented, with every
  guarantee in `specs/orleans-execution` kept by the integration suite.
- The known limitations closed, not documented away.
- A migration a consumer can perform in one release: schema first, roles one by one, both models
  running during the rollout.

**Non-Goals:**

- Deprecating or removing the bus workers, their composites or their topics.
- Any provider beyond PostgreSQL native and the portable counter.
- Orleans streams, Orleans persistence for domain state, or a second runtime.
- Changing what `ISaga`, `IProjection`, `ICommandHandler<T>`, `IAggregateScopedCommand`,
  `IEventSource` or `ICommandOutboxDispatcher` promise.

## Decisions

### D1 — Two packages, both Tier-C, the ports where a consumer must reach them without the runtime

`Stratara.Orleans` holds the execution model: grains, the dispatcher, the completion queue, the
send lane, singleton work, durable timers, heavy work, the registrations. It references the Orleans
server and SDK packages and the framework's Tier-A and Tier-B packages plus `Stratara.Projections`
and `Stratara.Sagas`. `Stratara.Orleans.EntityFrameworkCore` holds the commit-order readers (native
PostgreSQL and portable), the checkpoint store, the model extension and the interceptor that
maintains the partition counter; it references `Stratara.EventSourcing.EntityFrameworkCore` and
`Stratara.Orleans`.

The ports a consumer implements or calls without hosting a silo — `IDurableTimers`,
`ITimerOwners`, `ITimerHandler`, `ISingletonWork`, `IRebuildableProjection`, `ISagaProcess` and
`SagaProcess<TState>`, `ICommittedPositionReader`, `IProjectionCheckpointStore`, `IProjectionRebuilder`
— move to `Stratara.Abstractions` (timers, singleton work, readers, checkpoints, rebuilder) and
`Stratara.Projections` / `Stratara.Sagas` (the rebuildable projection and the process), so that a
consumer's projection assembly does not reference the runtime to declare that it can be rebuilt.
This follows the rule the tier layout already states: every SPI lives in Abstractions, even where
its only implementation is in Tier-C.

*Rejected: one package.* A consumer on the bus workers who wants the commit-order reader for its
own purposes would take the runtime with it; and the persistence package is the one that changes
when a provider is added.

*Rejected: the ports staying in `Stratara.Orleans`.* A projection assembly would reference the
runtime to implement `IRebuildableProjection`, which is what the tier rule exists to prevent.

Evidence: `.claude/rules/architecture.md` → *Why the tiers are cut here*; the proof of concept's
project references.

### D2 — The registrations own the durable directory, and start-up checks it

The proof of concept let the host register the named directory (`GrainDirectories.Durable`) and
failed at the first activation if it did not. The shipped registration takes the directory:
`AddStrataraOrleans(silo, options => options.UseRedisDurableDirectory(connection))` — or any
`IGrainDirectory` factory — registers it under the name, and a silo lifecycle participant at the
`RuntimeInitialize` stage resolves the named directory and fails the silo with a message naming the
registration if it is absent. The default directory for aggregate and runner grains stays the
built-in one, which the archived B5 measured as the cheaper choice.

*Rejected: making Redis a hard dependency of the package.* The persistence package would then
carry a cache client for a consumer who backs the directory with something else; the factory
keeps the dependency at the host.

Evidence: archived `results.md` B5 (built-in +12 %, Redis as default +27 %); `GrainDirectories.cs`.

### D3 — Batch removal through the outbox repository port

`IOutboxRepository` gains `DeleteManyAsync(IReadOnlyList<Guid>, CancellationToken)` with a default
implementation that loops `DeleteAsync`; the Entity Framework Core repository overrides it with one
statement. The completion queue resolves the write unit of work and its repository, as the record
path does, and never sees a database context. A consumer's own repository keeps compiling.

Evidence: #77 review finding (archived `results.md` → *Still open*); `OutboxRepository.DeleteAsync`
already issues the same statement for one id.

### D4 — A resumed command is bounded, then kept

The outbox record gains an attempt count and a kept state. The drain resumes a stored command only
while its attempts are below the bound the host already configures for bus messages
(`MessageRetryOptions.MaxDeliveryAttempts`), increments the count on each hand-over, and on the
bound marks the record kept with the last failure — it is then invisible to the drain and visible to
an operator through the same surface the bus's dead-letter destination gives, a query and a return.
A hand-over that throws no longer aborts the pass: the drain records the failure and continues with
the next record.

*Rejected: a separate dead-letter table.* One record, one state; the outbox is already the durable
record and the bus path's dead-letter semantics are the ones to match.

Evidence: archived known limitation *A resumed intent has no bound*; `outbox-and-messaging` → *A
message a handler cannot take is retried a bounded number of times and then kept*.

### D5 — Diagnostics: one band, source-generated, on the framework's meter

`LogEvents.Orleans` takes band `117_000`: reader started and stopped, partition stalled (with
projection, partition and entry), entry retried, command recorded, resumed, kept, completion flush
failed, permit released by expiry, directory check failed. Every message is a `[LoggerMessage]` on
a partial class. Instruments on `ApplicationDiagnostics.Meter`: `orleans.reader.applied` (counter,
tags projection and partition), `orleans.reader.stalled` (up-down counter), `orleans.reader.lag`
(observable gauge, seconds since the oldest unapplied entry's commit where the reader knows it),
`orleans.intent.recorded`, `orleans.intent.resumed`, `orleans.intent.kept`,
`orleans.completion.flushed`, `orleans.completion.failed`, `orleans.heavy.permits_in_use`. Names are
part of the published contract from the first release that carries them.

Evidence: `observability` → *Instrument names are a stable published contract*; `LogEvents.cs`.

### D6 — Permits are leases reconciled against membership

The permit grain records, per permit, the holding silo and an expiry it extends while the work
runs. A permit whose silo the cluster has declared dead, or whose lease lapsed, is released on the
next acquisition attempt or by the grain's own timer. The worker renews its lease at half the lease
period while the unit runs; a unit that outlives its lease without renewing is a bug the log names.

*Rejected: a permit per silo counted from membership alone.* A silo that is alive but whose worker
hung would hold its permits forever.

Evidence: archived known limitation *Heavy-work permits are a counter without a lease*.

### D7 — Readers and processes are named, not typed

A committed position reader declares a stable name (`ICommittedPositionReader.Name`), and the
checkpoint store keys on it; the two shipped readers are `postgres-transaction-id` and
`partition-counter`. A saga process declares its name through the base class, defaulting to the
type's full name and overridable, and the state stream is keyed on it. Renaming a class changes
nothing a checkpoint or a stream is keyed on.

Evidence: archived known limitation *Reader and saga identities are simple type names*.

### D8 — Starters are silo lifecycle participants

The grain starters become `ILifecycleParticipant<ISiloLifecycle>` registered at the `Active`
stage, so they run when the silo can take a call, whatever order the host registered the silo and
the composites in. `CoHostingTests` keeps proving both orders.

Evidence: archived known limitation *The grain starters are hosted services*.

### D9 — Cancellation reaches the store

The reader loop, the checkpoint store, the durable timers and the saga process grain pass the token
they receive to every store call; a grain timer's token and a deactivation's token are the ones a
loop observes between batches.

Evidence: archived known limitation *Cancellation does not reach the store*.

### D10 — A batch says whether more exists

`CommittedBatch` gains `HasMore`; the readers already read one row past the batch and know. The
loop stops after a batch that was not cut short instead of issuing the empty read. A commit that
lands in between is a wake-up like any other.

Evidence: #77 review finding (archived `results.md` → *Still open*).

### D11 — The composites split additively

`AddEventProjectionServices` and `AddSagaServices` register what the worker composites register
minus the bus-fed worker; `AddEventProjectionWorkerServices` and `AddSagaWorkerServices` call them
and add the worker. The execution model's registrations require the services composite and register
nothing the worker composite registers, so a host that called the worker composite by mistake gets
both paths — which is the hybrid shape and works — instead of a silent removal.

Evidence: `host-composition` → *Each worker role has one composite that wires it*; the proof of
concept's `RemoveHostedServices` by name.

### D12 — The runtime version is a range, an Orleans minor is a Stratara patch

The packages depend on `Microsoft.Orleans.*` at `[10.3.1, 11.0.0)`. `Directory.Packages.props` pins
the build to one version inside the range; moving that pin is a dependency update, and shipping it
is a patch unless the wire format or a public Orleans type the packages expose changed.

Evidence: archived open question Q5 and its answer in the archived `results.md`.

### D13 — Documentation is derived from the capability, and says which model is recommended

`docs/` gains the capability's page, a migration page from the archived migration note, and an
operations page from the archived operations note — including the hard-death scenario with its three
answers, verbatim in substance. `llms.txt` gains the core facts; the landing page and `README.md`
name the Orleans execution model as the recommended one and the bus workers as supported.

Evidence: `package-distribution` → *Documentation never names an API that does not exist*; the
archived notes.

### D14 — Evidence before the tag

B3 and B5 run once on the packaged code with diagnostics on, under the production profile and the
built-in directory, and are compared with the archived optimised numbers; the expectation is within
10 % of them. The integration suite runs against the packages in the same run as the gauntlet's
sibling workflow. The raw output goes to this change's `evidence/`.

## Risks / Trade-offs

- **[A consumer migration touches the outbox and event stream tables]** → Additive columns with
  defaults; the migration note lists them and the order; a host on the bus workers behaves as before.
- **[Ports move between packages]** → Type-forwarders where a public type moves from a package a
  consumer might already reference; the proof of concept was never published, so nothing a consumer
  installed moves.
- **[Diagnostics cost on the hot path]** → D14 measures it; the archived numbers are the bound.
- **[The lease adds a renewal call per heavy unit]** → At half the lease period, bounded by the unit's
  duration; measured in the heavy-burst test's latency bounds.
- **[Single-silo deployments and a hard death]** → Not fixable here; the operations page states it
  and the capability says so in a scenario.
- **[Scope]** → Fourteen decisions and eight spec deltas. The tasks are ordered so that the packages
  can ship with D1–D5 and D11–D13 if time presses, with D6–D10 as the tail; the owner decides at
  task 6.1 whether a tail item may follow in a patch.

## Migration Plan

For a consumer, in this order, one release at a time if it wishes:

1. Upgrade the packages; generate and apply the migration (commit-order columns, counter table,
   outbox bookkeeping, checkpoint table). Nothing changes yet.
2. Add the silo with storage-backed clustering and reminders on the store's database and the
   durable directory; apply the three Orleans scripts at the pinned tag.
3. Adopt roles one by one — projections first (both models run; the grain path applies
   idempotently beside the bus), then sagas, then the command dispatcher and the drain, then heavy
   work — and remove each role's bus worker host when its grain path has run a release.
4. Run two silos, or a stable endpoint, before relying on a hard death being survivable.

Rollback per role: remove the registration, redeploy the worker host; the checkpoints stay and are
ignored. Rollback of the schema: not needed; the columns are inert without the model.

## Open Questions

None that change the tasks. Whether `Stratara.Orleans.EntityFrameworkCore` gains a SQL Server
native reader is a later change; the portable counter serves SQL Server today.
