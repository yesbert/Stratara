# Design — Prove an Orleans execution model

## Context

See `proposal.md` → *Why* for the motivation. This section records the state the proof of concept starts
from, and where the hand-off it is based on had to be corrected.

The hand-off was verified by its authors against `0c36446`. `main` is at `1476af9`; the only commit in
between touches `.mcp.json`, so every source line cited below is unchanged. Each claim was re-read
against the source on 2026-09-13.

### What the source says today

- **Command side.** `MediatorCommandWorker` (`src/Stratara.Outbox.RabbitMQ/Mediator/MediatorCommandWorker.cs`)
  runs N subscriptions on one queue and, for an `IAggregateScopedCommand`, takes a bucket lock
  (lines 132-137). The lock pool is a field of the worker instance (line 60), so serialisation holds per
  worker per process, never across processes. `mediator-dispatch` states the per-process guarantee.
- **Projection and saga side.** `ProjectionWorker` and `SagaWorker` subscribe to the bundle topic
  (`ProjectionWorker.cs:105`, `SagaWorker.cs:104`), lock per bucket (lines 151/152) and retry a
  missing prerequisite in process (lines 156/157). Neither keeps a checkpoint.
- **Sagas are stateless by contract.** `ISaga`'s documentation: "A new instance is resolved per event
  bundle, so cross-bundle state must be persisted externally" (`src/Stratara.Sagas/Abstractions/ISaga.cs`).
- **Replay.** `ProjectionReplayWorker` truncates every view and reads with `GetManyAfterSequenceAsync`
  (`src/Stratara.Projections/Services/ProjectionReplayWorker.cs:159`).
- **Sequence numbers.** `SequenceNumber` is the database-generated key
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/Configurations/EventStreamEntryConfiguration.cs:21-22`).
- **Provider.** `Stratara.EventSourcing.EntityFrameworkCore` references `Npgsql.EntityFrameworkCore.PostgreSQL`
  directly and describes itself as "EF Core persistence … on PostgreSQL". `Stratara.Infrastructure`, where
  `EventSource` lives, references `Stratara.Outbox.RabbitMQ`.
- **Version.** `<VersionPrefix>` is `4.0.3`; `CHANGELOG.md` has no unreleased entry. Orleans is referenced
  nowhere. The latest stable `Microsoft.Orleans.Server` is 10.3.1 (2026-08-28).

### Corrections to the hand-off

| # | Hand-off said | Source says |
|---|---|---|
| C1 | SF-001: `RabbitMqBus.cs` lines 246-253 reject with `requeue=false` | Lines 244-248 are the `ConcurrencyException` branch (`requeue=true`); the discard is lines 249-253. |
| C2 | SF-001: the discard is undocumented; consumers design for a dead-letter queue | Half true. `projections` → *A failing projection stops the bundle* already states it: "On the RabbitMQ transport, a bundle that fails for anything other than a concurrency conflict is rejected without requeue, so the read model is repaired by a replay". What is wrong is the `MediatorCommandWorker` XML remark (line 38, "dead-lettered"); the `sagas` and `outbox-and-messaging` specs are silent. |
| C3 | SF-001: the archived design states the unbounded requeue at lines 128-132 | The passage is lines 127-133 of `2026-09-02-let-no-fact-overtake-its-beginning/design.md`; the quoted sentence is lines 129-131. The same design (line 27) already recorded "Everything else is `requeue=false`" and left the transports out of scope deliberately. |
| C4 | SF-001: the failure is recoverable on Azure Service Bus | Confirmed, and the asymmetry is wider: Azure Service Bus dead-letters a failure (`AzureServiceBusBus.cs:101`) and *abandons* a concurrency conflict (line 96), which the broker bounds by its maximum delivery count. RabbitMQ neither dead-letters the one nor bounds the other. |
| C5 | SF-003: `EventSource` recognises a conflict through `PostgresException` | Overstated. `IsConcurrencyOrUniqueViolation` (`EventSource.cs:182-208`) also recognises `ConcurrencyConflictException` and `DbUpdateConcurrencyException`, which are provider-neutral. The claim holds for the case that matters: an append is an insert, a duplicate version is a unique violation, and only the PostgreSQL branch (line 200) recognises that. |
| C6 | SF-003: the SQLite behaviour is unverified | Was unverified: the only test (`tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceTests.cs:274`) throws a mocked `PostgresException`. **Verified on 2026-09-13 by task 12.1**: `EventSourceSqliteConcurrencyTests` appends the same version twice on the SQLite host and gets a `DbUpdateException` over a UNIQUE violation, not `ConcurrencyException`. The finding holds. |
| C7 | Hard requirement: the event store "is written against EF Core and must stay replaceable" | Not a current property. The store package takes a hard dependency on the PostgreSQL provider, and no spec requires provider neutrality — `event-sourcing-store` requires only that the unique constraint exists. Database neutrality is therefore a design goal for new code in this change, not a guarantee to preserve. |
| C8 | §5: the replay worker is unaffected by commit order because publication is suppressed | The replay reader can skip a late-committing entry like any "after sequence" reader. It is covered because that entry's bundle is suppressed into durable storage and drained after the replay (`EventBundleOutboxDispatcher.cs:44`, `:59`). The cover only reaches hosts that share the replay coordination state (`projections` → *Publication is suppressed while a replay is active*). |
| C9 | Consumer file paths and consumer names as evidence | Not verifiable from here, and not allowed in this repository: Stratara never references a consumer application, and no file here may point outside the repository. This change names no consumer and carries the substance instead. |

Everything else checked holds: `BusEnvelopeIntegrityMode.cs:27`, `MediatorCommandWorker.cs:38`,
`RabbitMqBus.cs:213-214`, `EventSource.cs:45/155/170/210-219`, `EventBundleOutboxDispatcher.cs:42-54`,
`UnitOfWork.cs:17-21`, and the deprecation rule in
`2026-08-30-retire-the-deprecated-members/proposal.md:11-12`.

## Goals / Non-Goals

**Goals:**

- Evidence, not an opinion, for two decisions: offer Orleans as an *additional* execution model, and make
  it the *recommended* one.
- A commit-order reader that no test can make skip an entry, on PostgreSQL and on a portable fallback.
- A migration note concrete enough that a consumer can estimate the work: registrations that change,
  guarantees that get stronger, handler assumptions that become unnecessary but stay harmless.

**Non-Goals:**

- Shipping anything. No packable project, no store schema change, no spec delta, no deprecation.
- Fixing SF-001, SF-002 or SF-003 on the bus path. They are decided here and fixed in their own changes.
- SQL Server native reading, unless everything else is done.
- Consumer vocabulary — process grains, capabilities, roles — which belongs to consumers.

## Hard constraints

These come from the hand-off and are accepted as stated, with C7 in mind.

1. **No break in 4.x.** Additive only. A later deprecation follows the existing rule: `[Obsolete]` naming
   the successor with a migration sentence, at least one minor version of overlap, removal no earlier than
   the next major.
2. **No silent change of semantics.** `ICommandOutboxDispatcher.EnqueueCommandAsync` promises that an
   accepted command is not lost (`outbox-and-messaging` → *Dispatch attempts the bus first and falls back
   to durable storage*). An Orleans implementation behind that interface keeps the promise through a
   recorded intent and a timer, or does not implement the interface. A grain call alone is a remote call
   and is lost if the host dies before the append.
3. **Database neutrality through ports.** Anything provider-specific sits behind a port selected by
   registration, the way the transport is. This covers the commit-order reader and concurrency detection.
4. **The event stream is the only truth.** No grain storage for domain state; grains rehydrate by folding.
   Checkpoints, timers and intents are infrastructure state.
5. **Every guarantee in `projections` and `sagas` holds on the Orleans path**: idempotent apply without
   masking real conflicts, one at a time per aggregate, the missing-prerequisite report, discovery by
   assembly, the recorded session when applying.
6. **A storage-backed grain directory**, so single activation does not depend on a calm cluster; and a
   proven registration order for Stratara and Orleans in one host.
7. **Clean shutdown and one reset**: timers, membership and checkpoints can be cleared deterministically,
   and a hard kill never leaves a timer firing against state that is gone.

## Decisions

### D1 — The proof of concept is not packable

`src/Stratara.Orleans` keeps the repository default `IsPackable=false` and stays out of
`Stratara.Publish.slnf`.

*Rejected: a packable preview package.* Lockstep packs every packable project at a tag, so the next
release — including a patch nobody meant for Orleans — would publish a surface this change has not
decided on. A prerelease tag is available once the recommendation exists; that is a follow-up.

Evidence: `openspec/config.yaml` → `context` (packable opt-in, lockstep).

### D2 — No spec delta

The change sets `skip_specs: true`. It produces evidence; the guarantees an Orleans path would carry are
what the evidence decides. A follow-up change that productises the path writes the delta.

*Rejected: a speculative `orleans-execution` capability now.* It would specify the answer before the
question is measured, and `openspec validate` would accept it anyway.

### D3 — Evidence lives in the change

`evidence/expectations.md` is committed before the first run of any measurement; raw output goes to
`evidence/raw/<measurement>/<timestamp>/` with the commit, the hardware, the container images and the
package versions; `evidence/results.md` interprets them. After archiving, the data sits beside the
decision it supports.

*Rejected: extending `tests/Stratara.Benchmarks`.* It would pull Orleans into a benchmark project every
other measurement shares, and the results would outlive their context.

### D4 — Commit order behind one port

A *committed position reader* returns entries after a position, with the promise that no entry at or below
the highest returned position can still commit later. Implementations, in build order:

| Implementation | Mechanism | Role |
|---|---|---|
| Naive | `SequenceNumber > checkpoint` | Baseline; expected to fail the interleaving test |
| Safety window | only entries older than a delay | Baseline; no guarantee under long transactions |
| Portable counter | a position row per partition, incremented and locked inside the append transaction | Mandatory fallback and yardstick; any relational database |
| PostgreSQL native | transaction id (`xid8`) per entry; **ordered by it**, read only below `pg_snapshot_xmin(pg_current_snapshot())` | First production candidate |
| SQL Server native | `rowversion` and `MIN_ACTIVE_ROWVERSION()` | Only if time allows |

The portable counter orders **per partition** — a configurable fold of the 4096 buckets, 16 by
default — not per bucket: the counter row's lock serialises appends in one partition, which is what
makes commit order equal position order, and is also its throughput ceiling. A reader built on it
checkpoints per partition, which is the unit Q2 settles on. In the proof of concept the counter is
maintained by an EF Core save interceptor on the write context, so the shipped write path is untouched;
a shipped version would do the same from the unit of work.

*Found while building the native reader (2026-09-13):* the transaction id and the sequence number are
assigned in two separate atomic steps, so a transaction can hold the **lower id and the higher sequence
number**. A reader that orders by sequence number and merely filters by `xmin` still skips in that
case. The native reader therefore uses the transaction id as its position and orders by it, with the
sequence number only as the order within one transaction — and it never ends a batch in the middle of
one transaction's entries. The hand-off's sketch (§5) did not say this; the test in task 4.2 would
have.

Both non-baseline implementations need store columns the shipped model does not have. They live in a
PoC-only model extension used by the integration tests. Shipping one changes `event-sourcing-store` →
*The store declares its own schema* and needs a consumer migration, so it is a follow-up change.

*Rejected: a global counter row.* It serialises every append in the store on one lock.

### D5 — The aggregate grain sits behind the mediator, and two dispatch shapes are measured

The grain, keyed by aggregate id, calls the unchanged handler through `IMediator` under the recorded
session. Its turn replaces the bucket lock. Two shapes are built:

- **Synchronous:** an endpoint dispatches through the mediator; the call reaches the grain and the
  caller waits for the append. This does not implement `ICommandOutboxDispatcher`.
- **Durable intent:** `EnqueueCommandAsync` records the command durably before returning, calls the grain,
  and a timer (D6) replays an intent that never completed. Only this shape may implement the interface
  (constraint 2).

*Rejected: implementing `ICommandOutboxDispatcher` with a bare grain call.* Silent change of semantics.

*Found while building (2026-09-13):* **Orleans makes no promise about message order.** The
`[Unordered]` attribute is obsolete in Orleans 10 with the note "message ordering is not guaranteed
regardless of whether this attribute is used". The first run of the arrival-order test reordered 420
of 500 pairs; ordering the *sends* did not help (425 of 500), because calls in flight at the same
time are delivered in any order. The hand-off's premise — "ordering across two quick commands is
preserved, which a competing-consumer queue does not promise" — is wrong for Orleans as much as for
the queue: the grain's turn serialises, it does not order. What holds is what the caller enforces:
the proof of concept adds a scoped *send lane* that issues each call to an aggregate only after the
previous call to it from the same scope has completed, established synchronously at initiation. The
promise is per scope — one request, one handler, one unit of work — and it costs one round trip per
command on one aggregate, which is what the grain would take anyway. Across scopes nothing is
promised, as before. On the durable-intent shape a resumed intent keeps its order because the drain
reads intents in the order they were recorded.

*Also found:* a test assembly cannot host the process the tests kill. Running the host from a module
initializer deadlocks — the initializer holds the module lock, and the first continuation that
touches the module on another thread waits for it forever. The killed-and-restarted host is the
benchmark executable in a `--poc-host` mode, one process the tests start, drive over stdin and kill.

### D6 — The durable timer is an owner-checked reminder behind a Stratara port

A timer is registered with an owner id and a purpose. When it fires, the handler checks the owner first
and unregisters itself if the owner is gone; the operation that ends an owner cancels its timers in the
same turn. `IRemindable` appears on no consumer-facing type.

Risk to settle in task 1: Orleans reminders have a minimum period (one minute by default). Wake-ups
faster than that use a grain timer for speed with the reminder as the safety net.

### D7 — Storage-backed grain directory: Redis first

Stratara already depends on `StackExchange.Redis` (`Stratara.Infrastructure.csproj`). If an ADO.NET
grain directory exists for Orleans 10.3.1, it is measured as the database-only alternative; task 1
establishes whether it does.

### D8 — The projection grain applies through the existing projection pipeline

The grain reads through the port (D4), maps entries to events, sets the session recorded with each entry,
and applies through the same projection manager the replay worker uses. Consequences for the
`projections` guarantees:

- *one at a time per aggregate*: holds a fortiori — one grain applies one batch at a time;
- *idempotent apply without masking conflicts*: unchanged, the helpers are the same;
- *missing prerequisite*: a fact from another stream may not be applied yet even in commit order (a
  different bucket, a different grain). The grain retries under the existing named policy and does not
  advance its checkpoint past the entry;
- *discovery by assembly* and *recorded session*: reused, not reimplemented.

A commit hint wakes the grain (Q3); a timer is the safety net.

*Found while building (2026-09-13):* the framework's projection handler opens no transaction — each
projection writes inside its own `HandleAsync` however it chooses, through the read unit of work,
its own context, or memory. A checkpoint "in the read model's transaction" therefore cannot be
written generically. The grain writes it after the batch, in a transaction of its own, and the gap
between the two is covered by the guarantee the `projections` capability already gives: a projection
applies an event whose effect is already present without failing. That settles Q2's second half and
is the same at-least-once contract the bus path has always had.

The projection services and the two bus-fed workers are registered by one call
(`AddProjectionWorker`), and the services are internal. The grains take the same registration and
remove the two workers by name — the consumer's migration is the composite it already calls plus one
call. A shipped version splits that registration; it is a productisation item, not a PoC concern.

### D9 — Heavy work records intent before hand-off and completion after

A heavy unit of work is recorded as an intent, handed to a bounded `[StatelessWorker]` grain, and marked
complete after. A crash in between leaves an intent that the timer (D6) resumes; the handler must
therefore tolerate a second run, as a bus consumer already must under at-least-once delivery.

### D10 — Stop criteria

Work stops and is reported when:

- the checkpoint path loses an event and the cause is not understood within the session;
- the portable counter cannot reach a usable throughput for a single bucket (the threshold is
  pre-registered in `evidence/expectations.md`);
- Stratara and Orleans cannot be registered in one host without changing an existing composite.

## Tests and benchmarks

The hypotheses below are starting points. Task 2 turns each into a pre-registered expectation with a
concrete threshold, which the owner confirms before the first run. A threshold written after a run is not
a threshold.

**Runs.** Local machine, Docker (Testcontainers PostgreSQL and RabbitMQ), bus workers and silo measured
on the same machine in the same session. Hardware, OS, image digests and package versions are recorded
with every raw result. **The owner is asked before every run that is expected to take longer than 30
minutes of wall-clock time, and before anything that uses a paid or cloud resource.**

### Correctness — pass or fail

| Test | Expected | Falsified if |
|---|---|---|
| Kill the host between commit and publish, repeated | bus path loses bundles; checkpoint path loses none | the checkpoint path misses one event |
| Concurrent long transactions with interleaved commits | naive reader skips entries; safety window skips under a long transaction; portable and native skip none | a portable or native reader skips one entry — or the naive reader never skips, which means the test does not provoke the interleaving |
| Two quick commands to one aggregate (approve, then cancel) | the aggregate grain applies them in arrival order every time | any reordering |
| Timer owner removed while a timer is due | the timer unregisters and does nothing | the handler runs against a missing owner |
| Hard kill with open timers, then restart | timers fire once against existing owners, never against removed ones | a timer fires for a removed owner |
| Interactive commands under a sustained heavy-work burst | interactive latency stays within its no-burst range | interactive commands queue behind heavy work |
| Duplicate stream version on the SQLite test host (SF-003) | a generic `DbUpdateException`, not `ConcurrencyException` — this pins the finding, it is not a fix | `ConcurrencyException` is raised, which withdraws SF-003's impact |

A kill is a process kill of a separately started host, not a disposed in-process test cluster, because
only the former leaves the state a crash leaves.

### Performance — against the bus workers on the same hardware

| Measurement | Compare | Starting hypothesis |
|---|---|---|
| Append throughput | current store, native reader schema, portable counter | portable counter slowest under many writers on one bucket; native within a small margin of today |
| Event to read-model latency, p50 and p99 | bus push, checkpoint catch-up with a commit hint, hybrid | push fastest; catch-up with a hint close enough for interactive views |
| Commands per aggregate | `MediatorCommandWorker` and the aggregate grain | grain equal or better, without requeue storms under contention |
| Rebuild duration | today's full replay and a per-projection rebuild | per-projection rebuild shorter, and other projections keep running |
| Resource use at idle and under load | bus workers and silo | silo baseline higher at idle, lower per unit of work under load |

## Findings

Each finding is carried in substance, with the verification from *Corrections*, and ends in a proposed
decision the owner takes in task 14.1.

### SF-001 — A failing handler's message is discarded on RabbitMQ, not dead-lettered

**Observed.** A handler that throws anything but `ConcurrencyException` has its message rejected with
`requeue=false` (`RabbitMqBus.cs:249-253`); worker queues are declared without dead-letter arguments
(`:213-214`), so the broker drops it. A `ConcurrencyException` is requeued without bound (`:244-248`).
Azure Service Bus dead-letters the first and abandons the second, bounded by the broker (C4). The same
failure is recoverable on one transport and lost on the other, while `outbox-and-messaging` → *The
transport is replaceable* says nothing above the transport depends on the broker.

**Documentation.** `projections` states the discard (C2). The `MediatorCommandWorker` remark says
"dead-lettered" (line 38); `BusEnvelopeIntegrityMode.cs:27` says "NACK-discard on RabbitMQ", which is
accurate. Consumers have read the worker remark and planned for a dead-letter queue that does not exist.

**Impact.** A command accepted through `ICommandOutboxDispatcher` whose handler fails once is gone,
after its caller was told it was accepted. A bundle whose saga fails is gone for that subscription; a
bundle whose projection fails is gone until a full replay. Only a log line records it.

**Options.** (a) A bounded retry, then a dead-letter destination an operator can inspect and replay, and
a bound on the concurrency requeue — on RabbitMQ that means quorum queues with a delivery limit or a
re-publish with an attempt header, both a topology or wire change. (b) Specify the discard as the
guarantee on every capability that meets it and correct the worker remark.

**Proposed decision.** (a), in its own change with deltas to `outbox-and-messaging`, `projections` and
`sagas`. The wrong XML remark is a one-file fix that does not wait for it. The Orleans path does not
consume from the bus and so sidesteps the finding; it does not resolve it for consumers who stay on the
bus.

**Decided by the owner, 2026-09-13:** (a). Change `dead-letter-what-a-handler-cannot-take`, proposed.

### SF-002 — Committing events and publishing them are two transactions

**Observed.** `EventSource.SaveChangesAsync` commits the entries (`EventSource.cs:155`) and publishes the
bundle afterwards (`:170`, implemented at `:210-219`). Publishing tries the bus and writes to the outbox
only if the bus rejects (`EventBundleOutboxDispatcher.cs:42-54`). Every unit of work opens a new
`DbContext` (`UnitOfWork.cs:17-21`), so the outbox insert is never part of the commit.

**Specification.** `outbox-and-messaging` → *Delivery is at least once, never at most once* covers "a
message that reaches durable storage". A bundle for committed events that reaches neither the bus nor
storage is covered by no requirement.

**Additional observation.** If the bus rejects and the outbox write then fails, `SaveChangesAsync` throws
after the commit: the caller sees a failure for facts that are stored, and a caller that retries repeats
an intent that already happened.

**Impact.** A process that dies between commit and publish leaves facts no projection and no saga ever
receives. Projections recover only by a full replay; sagas never recover. The loss is silent.

**Options.** (a) Write the outbox row in the commit's transaction — which inverts *Dispatch attempts the
bus first and falls back to durable storage* for bundles and costs a write on every append. (b) Consumers
read the store from a checkpoint in commit order, and the push becomes a wake-up hint — this change
measures it.

**Proposed decision.** Deferred to the benchmark result. If the checkpoint path is recommended, (b)
resolves SF-002 for it, and the bus path still needs (a) or an explicit spec statement of the gap for as
long as it is supported.

**Decided by the owner, 2026-09-13:** T1 measured the loss (20 of 20 on the bus, 0 of 20 on the
checkpoint path); (b) is offered as an additional execution model and closes the finding for its
consumers. For the bus path: a change that states the gap in `outbox-and-messaging` and evaluates (a).
Change `close-the-gap-between-commit-and-publish`, proposed.

### SF-003 — Concurrency detection depends on a PostgreSQL exception

**Observed.** A duplicate stream version is an insert that violates the unique index, and `EventSource`
recognises that only through `PostgresException` with SQL state `23505` (`EventSource.cs:45`, `:200`).
The provider-neutral branches exist but do not fire for an insert (C5).

**Impact.** On any other provider a duplicate version would surface as `DbUpdateException`, not
`ConcurrencyException`, and everything keyed on that exception — the concurrency retry, the requeue in
SF-001 — would behave differently. Unverified (C6). Its weight depends on C7: the store is PostgreSQL-bound
at the package level today, so the finding matters for the test host and for any future provider, not for
a shipped one.

**Options.** (a) Classify unique violations behind a provider port selected by the store registration,
the way the transport is selected. (b) Record PostgreSQL as the only supported store and scope the test
host accordingly.

**Proposed decision.** First pin it: the SQLite correctness test above. If it confirms the finding, (a)
in its own change, because the PoC's constraint 3 needs the same port for its own readers.

**Decided by the owner, 2026-09-13:** the test confirmed it (T7); (a). Change
`detect-a-conflict-on-any-provider`, proposed.

## Risks / Trade-offs

- **[`Stratara.Infrastructure` references `Stratara.Outbox.RabbitMQ`]** A bus-free Orleans host that reuses
  `EventSource` still takes the RabbitMQ package transitively. → Measured, not fixed: the PoC records what
  a bus-free host has to reference, and the recommendation says whether a package split is needed.
- **[Reminder minimum period]** Sub-minute wake-ups cannot rely on reminders alone. → D6.
- **[Portable counter ceiling]** One lock per bucket caps appends per bucket. → Measured; a stop
  criterion (D10).
- **[Orleans version coupling]** A consumer that already hosts Orleans must use the version Stratara was
  built against. → Q5; pinned in `Directory.Packages.props` for the PoC.
- **[Grain count]** One grain per projection per bucket at 4096 buckets is too many activations. → Q2
  groups buckets into partitions.
- **[Hard-kill tests are slow and flaky]** Process kills and restarts take seconds per iteration. →
  Iteration counts are pre-registered; a long run is asked for first.
- **[Scope]** Six blocks, four readers and a benchmark suite is a lot for a proof of concept. → Blocks are
  built only as far as their test needs; D10 stops early on the failures that decide the question.

**Known limitations of the proof-of-concept code**, found in the pre-merge review on 2026-09-13 and
left as they are because the code is evidence, not a package. A change that ships the execution
model must address each:

- *No diagnostics.* `Stratara.Orleans` raises no log event and no metric. An entry that keeps failing
  in `StoreReaderLoop.ApplyAsync` stalls its partition silently; the only signal is a checkpoint that
  does not advance. A shipped package logs it with an event id from `LogEvents` and counts it.
- *Heavy-work permits are a counter without a lease* (`HeavyWorkPermitGrain`). A worker silo that
  dies between acquire and release leaks a permit for the life of the permit grain's activation;
  under repeated worker crashes the cluster-wide limit shrinks to zero. A lease with an expiry, or a
  holder list the permit grain reconciles against cluster membership, is required.
- *A resumed intent has no bound.* `OrleansCommandDispatcher.ResumeAsync` hands every unfinished
  intent back to its grain on each drain pass; an intent whose handler always throws is retried
  forever and, because the hand-over is awaited, aborts the rest of the pass. The bounded retry and
  dead-letter of `dead-letter-what-a-handler-cannot-take` apply here too.
- *Reader and saga identities are simple type names.* Checkpoints are keyed on
  `reader.GetType().Name` and saga state streams on the saga's simple name. Two types with the same
  name in different namespaces collide, and renaming a reader class orphans every checkpoint.
  A shipped package names them explicitly.
- *The grain starters are hosted services.* `StoreReaderGrainStarter` and the singleton-work
  starter make grain calls from `IHostedService.StartAsync`; that the silo is up by then depends on
  `UseOrleans` being registered first. Orleans' own hook is a startup task on the silo lifecycle.
- *Cancellation does not reach the store.* `StoreReaderLoop`, `DurableTimers` and the saga process
  grain accept tokens and drop them before the reader and checkpoint calls.

## Migration Plan

Nothing is deployed and nothing is published, so there is nothing to roll back: removing the new
projects and the Orleans package pins undoes the change. The consumer migration note is a deliverable
(task 14.3), not a plan for this change.

## Open Questions

These six questions come from the hand-off. None changes the task breakdown: each is either decided by a
measurement the tasks already contain, or needed only for the follow-up change. Each has a leaning, so the
build does not start from nothing.

1. **Where does a stateful saga's state live without grain storage?** Its own event stream, which makes it
   an aggregate; or a timer-only saga that derives its state from the facts it reads. *Leaning:* its own
   stream keyed by correlation — it reuses rehydration, concurrency and the stream as truth. The
   timer-only variant is sketched for sagas whose state is fully derivable. *Decided by:* task 9.
2. **One projection grain per projection, or per projection and bucket, and how do a checkpoint and the
   read-model write stay atomic?** *Decided 2026-09-13 by tasks 4.1 and 8.1:* per projection and
   **partition** — a configurable fold of the buckets, 16 by default — because the portable counter
   orders per partition and 4096 grains per projection is too many. The checkpoint is **not** in the
   read model's transaction: the projection handler has no transaction to join (see D8), so the grain
   writes the checkpoint after the batch and relies on idempotent apply for the gap. The rebuild
   benchmark still decides whether 16 partitions is the right default.
3. **The wake-up hint after a commit.** A database notification, a light bus message, or a grain call from
   the event source. *Leaning:* a grain call inside one cluster, a database notification as a
   provider-specific option behind a port, a bus message only across clusters — always with the timer as
   the safety net. *Decided by:* the event-to-read-model latency benchmark.
4. **A cluster-wide limit for heavy work**, since a stateless worker's bound is per silo. *Leaning:* a
   singleton permit grain (token bucket) that workers acquire from; measured against "per-silo bound ×
   silo count". *Decided by:* task 10.
5. **How the Orleans version is pinned across a lockstep package family used by external consumers.**
   *Leaning:* a range bounded by the next Orleans major in the package's dependency, with an Orleans minor
   upgrade treated as a Stratara patch unless it changes the wire format. Not needed until productisation.
6. **Are the bus workers removed in the next major, or kept as a separate supported package?** Answered
   by the recommendation (task 14.2), not by the build.
