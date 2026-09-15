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

A review of the proof of concept after #77 (2026-09-14) found places where the code does not yet
keep the guarantees this change is about to publish, and places where this change's own artifacts
contradicted each other. They are closed here as well:

| Review finding | Closed by |
|---|---|
| An enqueued or resumed command skips the mediator pipeline, and the dispatcher registration replaces the enqueue-time authorizer | D15 |
| A command a handler sends for another aggregate runs outside that aggregate's activation | D16 |
| A process timeout whose handling touches its timers waits on itself until the call times out | D17 |
| A kill between a process step's append and its timer registration loses the timeout | D18 |
| A timer tick that arrives a moment early on a silo with another clock fires a whole retry period late | D19 |
| A command still running past the grace is handed over again on every drain pass; a resumed encrypted command loses its aggregate | D4 |
| The partition count is not part of a checkpoint's identity | D7 |
| A rebuild truncates before it resets checkpoints; a full replay leaves the store readers' checkpoints standing | D11, D21 |
| Invalid settings, timer ports registered after the model, singleton work placed on a silo that lacks it | D20 |
| The reset requirement has no design; the portable reader has no backfill for existing entries | D22, D23 |
| Public surface and provider coupling of the proof of concept; kill tests that pass by timing; the coverage exclusion that keeps the proof of concept out of the analysis gate | D24, D25, D26 |

Facts the design rests on, read 2026-09-14:

- `Directory.Build.props` → `<VersionPrefix>4.0.4</VersionPrefix>`, `IsPackable=false` by default,
  a packable csproj opts in and is listed in `Stratara.Publish.slnf` (25 projects today).
- `src/Stratara.Diagnostics/LogEvents.cs` allocates `100_000–116_999`; the next band is `117_000`.
- `src/Stratara.EventSourcing.EntityFrameworkCore` depends on the PostgreSQL provider directly
  (archived correction C7), so a reader native to PostgreSQL in the persistence package adds no
  dependency the package does not already have.
- `AddEventProjectionWorkerServices` and `AddSagaWorkerServices`, in
  `src/Stratara.EventSourcing.WorkerDefaults/WorkerDefaultsHostBuilderExtensions.cs`, register their
  bus-fed workers inside one call; the proof of concept removed them, and the replay worker with
  them, by implementation name.

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
SDK and runtime packages — not the server meta-package, which the host brings — and the framework's
Tier-A and Tier-B packages plus `Stratara.Projections` and `Stratara.Sagas`, and no Entity Framework
assembly: its registrations lose the `TReadContext : DbContext` type parameter the proof of concept
has. `Stratara.Orleans.EntityFrameworkCore` holds the commit-order readers (native PostgreSQL and
portable), the interceptor that maintains the partition counter, the checkpoint store and its
registration (`AddStrataraProjectionCheckpoints<TReadContext>()`), and the portable reader's backfill;
it references `Stratara.EventSourcing.EntityFrameworkCore` and `Stratara.Orleans`.

The schema additions are not in it. The shipped write and read contexts in
`Stratara.EventSourcing.EntityFrameworkCore` declare the commit-order column (PostgreSQL only, behind
the provider switch there), the position column, the counter table, the outbox record's resume
bookkeeping and the checkpoint table, and the entities for the counter and the checkpoint live beside
them — because *The store declares its own schema* requires the shipped model to carry them, and that
package cannot reference the Orleans persistence package. (Owner decision, 2026-09-15, during apply.)

The ports a consumer implements or calls without hosting a silo — `IDurableTimers`,
`ITimerOwners`, `ITimerHandler`, `ISingletonWork`, `IRebuildableProjection`, `ISagaProcess` and
`SagaProcess<TState>`, `ICommittedPositionReader`, `IProjectionCheckpointStore`, `IProjectionRebuilder`,
`ICommandIntentStore` — move to `Stratara.Abstractions` (timers, singleton work, readers, checkpoints,
rebuilder, command intents) and
`Stratara.Projections` / `Stratara.Sagas` (the rebuildable projection and the process), so that a
consumer's projection assembly does not reference the runtime to declare that it can be rebuilt.
This follows the rule the tier layout already states: every SPI lives in Abstractions, even where
its only implementation is in Tier-C.

*Rejected: one package.* A consumer on the bus workers who wants the commit-order reader for its
own purposes would take the runtime with it; and the persistence package is the one that changes
when a provider is added.

*Rejected: the ports staying in `Stratara.Orleans`.* A projection assembly would reference the
runtime to implement `IRebuildableProjection`, which is what the tier rule exists to prevent.

*Rejected: an opt-in model extension in the Orleans persistence package.* A consumer who does not call
it would migrate a schema that lacks what the store is specified to declare.

Evidence: `package-distribution` → *Dependencies flow one way and never cycle*; the proof of
concept's project references and `OrleansProjectionServiceCollectionExtensions.cs:40-44,90,149`
(the `DbContext` constraint and the checkpoint store registered in the runtime package).

### D2 — The registrations own the durable directory, and start-up checks it

The proof of concept let the host register the named directory (`GrainDirectories.Durable`) and
failed at the first activation if it did not. The shipped registration takes the directory:
`AddStrataraOrleans(silo, options => options.DurableDirectory(factory))` takes an `IGrainDirectory`
factory and registers it under the name — a Redis-backed directory is two lines of host code the
operations page shows, not a helper in either package — and a silo lifecycle participant at the
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

The outbox record gains an attempt count, a kept state, the time of its last hand-over, the last
failure, and the aggregate id and heavy flag the dispatcher already knows when it records the command.
The operations on that bookkeeping sit behind a port of their own, `ICommandIntentStore` in
`Stratara.Abstractions`: record a command with its aggregate and heavy flag, list the recorded commands
due for resumption, claim a hand-over atomically (stamping its time and incrementing the count), renew
the hand-over while the handler runs, record a failure, and keep a command.
`Stratara.Orleans.EntityFrameworkCore` implements it and registers it with
`AddStrataraIntentStore<TWriteContext>()`; the execution model's dispatcher requires it, and a host
without it fails at start. `IOutboxRepository` gains only the batch removal of D3.

The drain resumes a stored command only while its attempts are below the bound the host already
configures for bus messages (`MessageRetryOptions.MaxDeliveryAttempts`) and its last hand-over is older
than the grace. A running handler renews its hand-over at half the grace, so it is not handed over
again however long it runs. On the bound the drain marks the record kept with the last failure; it is
then invisible to the drain. The aggregate is
read from the record, not from the command's JSON, so a command whose payload is protected keeps its
per-aggregate order when resumed. An operator finds kept commands by the log event and the counter of
D5 and returns one by clearing its kept state and attempt count, a single statement the operations
page shows; the drain then resumes it with its count starting over. A hand-over that throws no longer
aborts the pass: the drain records the failure and continues with the next record.

*Rejected: a separate dead-letter table.* One record, one state; the outbox is already the durable
record and the bus path's dead-letter semantics are the ones to match.

*Rejected: default-implemented members on `IOutboxRepository`.* A consumer's own repository would keep
compiling and silently lose the bound, because a default cannot persist the bookkeeping; a missing port
fails at start instead. (Owner decision, 2026-09-15, during apply.)

*Rejected: reading the aggregate id from the payload.* A payload sealed with `[EncryptData]` or a
command that implements the id explicitly hides it, and the command then runs beside live commands
for its aggregate.

*Rejected: an operator API for returning kept commands.* One documented statement is what operators
of the bus path already do with the broker's tools; an API is a later change if consumers ask.

Evidence: archived known limitation *A resumed intent has no bound*; `outbox-and-messaging` → *A
message a handler cannot take is retried a bounded number of times and then kept*;
`OrleansCommandDispatcher.cs:66-72` (the drain filters on the record's creation time only) and
`:108-115` (the aggregate id parsed from JSON); `SecureJsonSerializer.cs:17-22,63-68`.

### D5 — Diagnostics: one band, source-generated, on the framework's meter

`LogEvents.Orleans` takes band `117_000`: reader started and stopped, partition stalled (with
projection, partition and entry), entry retried, a catch-up started by a wake-up faulted, command
recorded, resumed, kept, completion flush failed, permit released by expiry, directory check failed. Every message is a `[LoggerMessage]` on
a partial class. Instruments on `ApplicationDiagnostics.Meter`: `orleans.reader.applied` (counter,
tags projection and partition), `orleans.reader.stalled` (up-down counter), `orleans.reader.lag`
(observable gauge, seconds since the time recorded with the oldest unapplied entry, for both readers),
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

The portable reader's name carries the partition count it positions under (`partition-counter/16`),
and the interceptor reads the count from `CommitOrderOptions` instead of a constructor argument of
its own, so a host that changes the count is refused at the checkpoint with a message naming both
counts instead of skipping entries under a remapped partition.

Evidence: archived known limitation *Reader and saga identities are simple type names*;
`StoreReaderLoop.cs:111,133` and `SagaProcessGrain.cs:123` (identities from `GetType().Name`);
`ProjectionCheckpoint.cs:83-87` (the reader-name refusal); `PartitionCounterInterceptor.cs:52`.

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

`CommittedBatch` gains `HasMore`; the native reader already reads one row past the batch
(`PostgresTransactionIdReader.cs:52`, `LIMIT batchSize + 1`), and the portable reader, which takes
exactly the batch today (`PortableCounterReader.cs:35`), gains the same. The
loop stops after a batch that was not cut short instead of issuing the empty read. A commit that
lands in between is a wake-up like any other.

Evidence: #77 review finding (archived `results.md` → *Still open*).

### D11 — The composites split additively

`AddEventProjectionServices` and `AddSagaServices` join `Stratara.EventSourcing.WorkerDefaults`
beside the composites they come from, as `IHostApplicationBuilder` extensions, and register what
`AddEventProjectionWorkerServices` and `AddSagaWorkerServices` register minus the bus-fed worker. The
projection replay worker stays in the services composite: a full replay is still how a projection
that cannot rebuild alone is rebuilt. The worker composites call the services composites and add the
worker. The execution model's registrations require the services composite and register nothing the
worker composite registers, so a host that called the worker composite by mistake gets both paths —
which is the hybrid shape and works — instead of a silent removal.

A full replay on a host with store-reading projections pauses their readers, returns their
checkpoints to the beginning once it has truncated, and resumes them when it ends. Without that, a
reader would resume past entries whose effect the truncation removed, and its read model would stay
partly empty.

Evidence: `host-composition` → *Each worker role has one composite that wires it*;
`WorkerDefaultsHostBuilderExtensions.cs:121,146`; the proof of concept's `RemoveHostedServices` by
name, which removes the replay worker too (`OrleansProjectionServiceCollectionExtensions.cs:61`).

### D12 — The runtime version is a range, an Orleans minor is a Stratara patch

The two package projects declare `Microsoft.Orleans.*` with `VersionOverride="[10.3.1, 11.0.0)"`,
because central package management refuses a `Version` on a `PackageReference`; the build resolves
the floor, which is the central pin at 10.3.1 that test and benchmark projects keep. Moving the pin is
a dependency update, and shipping it is a patch unless the wire format or a public Orleans type the
packages expose changed.

Evidence: `Directory.Build.props` (`ManagePackageVersionsCentrally=true`); the archived
`2026-09-13-prove-an-orleans-execution-model/design.md` → open question Q5 and its leaning.

### D13 — Documentation is derived from the capability, and says which model is recommended

`docs/` gains the capability's page, a migration page from the archived migration note, and an
operations page from the archived operations note — including the hard-death scenario with its three
answers, verbatim in substance. `llms.txt` gains the core facts; the landing page and `README.md`
name the Orleans execution model as the recommended one and the bus workers as supported.

The change also touches every place that states a number it moves: the package count, 25 to 27, in
`llms.txt`, `README.md`, the preamble of `CHANGELOG.md`, `docs/index.md`, `docs/overview/packages.md`,
`docs/overview/architecture-at-a-glance.md`, `openspec/config.yaml` and
`.github/copilot-instructions.md`; the allocated log-event range on
`docs/reference/log-events-schema.md` and in `LogEvents.cs`; a cheatsheet row for every new
registration; the comment on the Orleans pins in `Directory.Packages.props`. `llms-full.txt` is
generated from the assemblies by `tools/Stratara.ReferenceCatalogue`, not edited.

Evidence: `package-distribution` → *Documentation never names an API that does not exist*; the
archived notes; `LogEventAllocationTests`, `DiCheatsheetCoverageTests` and
`ReferenceCatalogueIsCurrentTests` in `tests/Stratara.Documentation.Tests`.

**Presented as a headline feature (owner request, 2026-09-14).** Naming the recommended model is not
enough: the owner asked that the execution model be presented as the release's headline feature on
every entry point a reader arrives through. `README.md` gains a door beside the existing three that
leads with the benefit the capability specifies, a step in *It grows with you*, and the measured rows
in *Numbers, not adjectives*; the landing page carries the same door, a feature card in *What is in
the box* and the same numbers; `docs/getting-started/` gains *Choose an execution model*, which links
the capability, migration and operations pages; the minor's `CHANGELOG.md` section opens with it, and
that section is the GitHub release note. Every claim is one a requirement of `orleans-execution` or a
number in `evidence/results.md` supports, and a number measured on the proof of concept is not quoted
as a number of the packages — the rows come from task 7.3.

*Rejected: a separate marketing page.* It would restate the capability in a second voice and drift
from it; the entry points present the feature and link to the derived pages instead.

Evidence: the owner's request of 2026-09-14; `README.md` and `docs/index.md` section structure
(*Pick your door*, *It grows with you*, *What is in the box*, *Numbers, not adjectives*);
`release.yml` → `announce` publishes the version's `CHANGELOG.md` section as the release note.

### D14 — Evidence before the tag

B3 and B5 run once on the packaged code with diagnostics on, under the production profile and the
built-in directory, and are compared with the archived optimised numbers; the expectation is within
10 % of them. The integration suite runs against the packages in the same run as the gauntlet's
sibling workflow. The raw output goes to this change's `evidence/`.

### D15 — Commands meet the mediator pipeline and the enqueue-time authorizer on every path

The aggregate grain, the command runner and the heavy worker dispatch through `IMediator` with the
turn marked, instead of resolving the handler themselves, so validation, authorization, tenant
isolation, command audit and resilience run for a command on this path as they do in the bus's
command worker; the forwarding behaviour already lets a marked turn through. The enqueue-time
authorizer decorates whichever `ICommandOutboxDispatcher` is registered last instead of the bus
dispatcher's concrete type, and the execution model's dispatcher registration replaces only an
undecorated slot, so the two compose in either registration order.

*Rejected: documenting the intent path as handler-only.* The capability promises unchanged handlers;
a command that is validated on one path and not on the other is a security difference a consumer
cannot see from the call site.

Evidence: `AggregateGrain.cs:97-99` (the handler resolved directly); `MediatorCommandWorker.cs:164-165`
(the bus path goes through `IMediator`); `OrleansAggregateServiceCollectionExtensions.cs:75` (a plain
registration); `AuthorizingCommandOutboxDispatcherServiceCollectionExtensions.cs:34-40` (decorates the
bus dispatcher's type).

### D16 — The turn marker carries the aggregate it belongs to

The ambient marker that lets a command through inside a turn holds the aggregate id, and the
forwarding behaviour bypasses only a command for that aggregate. A command a handler sends for
another aggregate is forwarded to that aggregate's activation like any other.

*Rejected: forbidding cross-aggregate sends from a handler.* Handlers that issue follow-up commands
are an existing consumer pattern on the bus path.

Evidence: `AggregateGrainBehavior.cs:34`; `AggregateGrain.cs:108-124` (a boolean marker).

### D17 — The timer owner takes calls while it runs a due timer

The timer-owner grain is reentrant. Its only state is the reminder table, and registering or
cancelling by name is idempotent, so a register or a cancel that interleaves with a running tick
changes nothing a sequence would not. Without it a process timeout whose handling cancels or
reschedules a timer calls back into the grain whose turn waits on that handling, and the call times
out; a fact that reaches the process through the saga grain while the tick runs does the same.

*Rejected: call-chain reentrancy for the handler call only.* It covers the direct cycle and not the
fact that arrives from outside it.

Evidence: `TimerOwnerGrain.cs:58-81`; `SagaProcessGrain.cs:103-117,238-246`; `TimeoutSaga.cs:23` (the
test process completes on expiry and cancels its timers).

### D18 — A process step registers its timers before it records its events

The process grain applies a step's timer changes before it appends the step's events. A kill after
the timers and before the append leaves a timer whose step is not recorded; the fact is delivered
again, the step runs again and registers the same names idempotently, and a due timer whose owner has
no stream yet is dropped by the owner check. The process contract states that `OnTimeoutAsync`
decides from state, so a timeout its state does not expect is a no-op.

*Rejected: reconciling timers from state on every step.* State does not hold the timers a step
scheduled; it would need a second record.

Evidence: `SagaProcessGrain.cs:97` (the append) before `:114-117` (the registration);
`SagaProcessTimeoutTests.cs:38-41` (passes whether or not a timer existed before the kill).

### D19 — A tick within a tolerance of its due time is due

A tick that arrives earlier than the due time by less than a tolerance fires, because the due time
was computed on the registering host and the tick runs on a silo with a clock of its own. The
tolerance is an option on the durable timers with a default well below the retry period.

Evidence: `TimerOwnerGrain.cs:71-75`.

### D20 — Settings, timer ports and singleton placement are checked at start

Every options type the packages bind is validated at start: positive batch sizes, limits and partition
counts, periods at or above the runtime's reminder minimum, and an intent grace longer than the
completion window. The host's timer owners and handlers are collected as enumerable ports and composed
by owner prefix, so their registration order relative to the execution model does not matter, and
start-up fails when stateful processes are registered and no owner claims their prefix, and when the
command dispatcher is registered and no `ICommandIntentStore` is. Singleton work
is placed only on silos that registered it, through the runtime's placement filtering on silo
metadata.

*Rejected: last registration wins for the timer ports, with a documented order.* The failure is
silent — due timers unregister themselves — and nothing reports it.

Evidence: `StoreReaderLoop.cs:145-149` (a non-positive batch never advances); `HeavyWorkGrain.cs:51-64,84-88`
(a non-positive limit polls forever); `OrleansProjectionServiceCollectionExtensions.cs:109-145` (the
captured timer ports); `SingletonWorkGrain.cs:33-37` (activation fails where the work is absent).

### D21 — A rebuild resets checkpoints before it empties the read model

The rebuilder pauses the projection's readers, returns their checkpoints to the beginning, truncates,
and resumes them. A truncation that fails part-way leaves readers that re-read from the beginning over a
partly emptied model, which re-applying idempotently repairs.

Evidence: `ProjectionRebuilder.cs:59-68` (truncate, then reset, then resume in `finally`).

### D22 — One reset, behind a port

`IExecutionModelReset` in `Stratara.Orleans` clears reminders, membership, checkpoints and the durable
directory's entries. `Stratara.Orleans.EntityFrameworkCore` implements the storage part against the
store's database and the runtime's tables; the directory part is a callback the host supplies with its
directory factory, because the directory's backend is the host's choice (D2). The event stream is never
touched.

Evidence: `tests/Stratara.Orleans.IntegrationTests/Hosting/PocReset.cs` (the test-only reset, whose
remarks name this port); `orleans-execution` → *A host can reset what the execution model keeps outside
the event stream*.

### D23 — The portable reader positions existing entries once and refuses an unpositioned store

`Stratara.Orleans.EntityFrameworkCore` offers a backfill that positions every entry written before the
counter existed, per partition, in bucket-then-sequence order, and seeds the counter. The portable
reader checks at start that no entry lacks a position and fails with a message naming the backfill
otherwise. The native reader needs no backfill: the column's default stamps existing rows with the
migration's transaction id, which is below every later one.

*Rejected: positioning lazily on first read.* Every partition's reader would race the interceptor for
the counter lock at once, on the host's first start after the upgrade.

Evidence: `PortableCounterReader.cs:15-17` (unpositioned entries are invisible); the write model's
commit-order column default.

### D24 — The published surface is what a consumer should use

Before the packages ship: the two readers that deliberately do not keep the commit-order promise move to
the benchmarks; the send lane becomes internal and releases a scope's tail once it completes; the native
reader takes its table and column names from the model instead of assuming the snake-case convention;
every options type's section name is bound by its registration or no longer claims to be; a timer purpose
longer than the store's column is refused with an argument exception, not a provider exception; the
process base class documents that a fact may arrive twice and that a step which schedules a timer also
records an event; the drain does not read event bundles on a host whose bundle dispatcher stores none;
and the block the saga and projection grains share is written once.

Evidence: `NaiveSequenceReader.cs`, `SafetyWindowReader.cs` (documented as not keeping the promise);
`AggregateSendLane.cs` (public, unbounded `_tails`); `PostgresTransactionIdReader.cs:47-52,92-96`;
`OrleansTimersServiceCollectionExtensions.cs:17`; `TimerOwnerGrain.cs:98-106` and the reminder table's
`varchar(150)`; `OutboxDrainWork.cs:29-30`; the analysis workflow's duplication report
(`SagaGrain.cs`, `ProjectionGrain.cs`).

### D25 — Kill tests prove what they claim on a slow runner

The scenario host's path comes from build-time metadata with an environment-variable fallback instead
of the test output layout, and the integration project builds the host itself, so the CI step #80 added
for it goes. Kill tests set due times relative to a start signal, assert their preconditions before the
kill, and never pass on a restart that re-reads the store alone. New cases cover each finding D15–D21
closes, the takeover of singleton work by the surviving silo, and a timer across two silos.

Evidence: `PocHostProcess.cs:96-106`; `HardKillTimerTests.cs:41-58`; `OwnerCheckedTimerTests.cs:31-41`;
`SingletonWorkTests.cs:26-38`; `SagaProcessTimeoutTests.cs:38-41`; `.github/workflows/integration.yml`.

### D26 — The execution model returns to the coverage measure

The analysis workflow's coverage exclusion for `src/Stratara.Orleans/**` goes, and the analysis collects
coverage from the Orleans integration suite's in-process tests beside the unit tests, so the gate
measures the packages the way the suite verifies them. Kill tests run the silo in a second process and
stay uncounted.

*Rejected: keeping the exclusion.* It was right for a proof of concept nobody could install and is wrong
for a package that ships.

Evidence: `.github/workflows/sonar.yml` (the exclusion and its comment, #83).

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
- **[Scope]** → Twenty-six decisions and eight spec deltas. No decision is a tail: each closes a
  requirement a delta states, so a decision deferred to a patch takes its requirements and scenarios
  out of this change into a change of its own before approval (task 0.2), rather than archiving a
  guarantee the packages do not keep.
- **[A reentrant timer owner]** → Its state is the reminder table and register and cancel are
  idempotent by name; D17's test is the collision of a fact and a timeout.
- **[Timers registered before the append]** → A timeout may reach a process for a step whose events
  were not recorded; the process contract says `OnTimeoutAsync` decides from state.

## Migration Plan

For a consumer, in this order, one release at a time if it wishes:

1. Upgrade the packages; generate and apply the migration (commit-order columns, counter table,
   outbox bookkeeping, checkpoint table). Nothing changes yet. A host that will use the portable reader
   runs the backfill of D23 once before its readers start; on PostgreSQL the native reader needs none.
2. Add the silo with storage-backed clustering and reminders on the store's database and the
   durable directory; apply the three Orleans scripts at the pinned tag.
3. Adopt roles one by one — projections first (both models run; the grain path applies
   idempotently beside the bus), then sagas, then the command dispatcher and the drain, then heavy
   work — and remove each role's bus worker host when its grain path has run a release. A full replay
   during the rollout also returns the store readers' checkpoints to the beginning (D11).
4. Run two silos, or a stable endpoint, before relying on a hard death being survivable.

Rollback per role: remove the registration, redeploy the worker host; the checkpoints stay and are
ignored. Rollback of the schema: not needed; the columns are inert without the model.

## Open Questions

None that change the tasks. Whether `Stratara.Orleans.EntityFrameworkCore` gains a SQL Server
native reader is a later change; the portable counter serves SQL Server today.
