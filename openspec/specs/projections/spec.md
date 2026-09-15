# projections Specification

## Purpose
Turn the event stream into read models that a query can answer from directly, and be able to rebuild
those models from scratch when their shape changes — without the write side knowing they exist.

## Requirements

### Requirement: A projection declares the events it cares about by handling them

A projection SHALL be dispatched only the events it declares handlers for, determined from the
handler signatures themselves rather than from a separate registration.

#### Scenario: A bundle contains a mix of events

- **WHEN** a bundle contains events a projection handles and events it does not
- **THEN** only the handled ones are passed to it

#### Scenario: A bundle contains nothing a projection handles

- **WHEN** no event in a bundle is relevant to a projection
- **THEN** that projection is not invoked at all, and the skip is recorded at debug level

#### Scenario: Several projections are registered

- **WHEN** a bundle arrives and several projections are registered
- **THEN** each is offered the bundle and each receives its own relevant subset

### Requirement: A handler may take the event payload or the enveloped event

A projection handler SHALL be invoked with the event payload where it declares one, and with the
enveloped event where it declares that instead, so a projection that needs the event's metadata can
have it.

#### Scenario: The projection declares a payload handler

- **WHEN** a relevant event arrives and the projection declares a handler taking its payload
- **THEN** that handler is invoked with the payload

#### Scenario: The projection declares only an enveloped handler

- **WHEN** the projection declares no payload handler but declares one taking the enveloped event
- **THEN** that handler is invoked with the envelope

### Requirement: A failing projection stops the bundle

Where a projection handler fails, the failure SHALL propagate rather than being swallowed, so the
bundle is not acknowledged and is not treated as processed. What happens to the bundle after that
is decided by the transport's failure policy, stated in `outbox-and-messaging` → *A message a
handler cannot take is retried a bounded number of times and then kept*: the bundle is delivered
again a bounded number of times and then moved to the subscription's dead-letter destination, from
which an operator returns it once the cause is fixed. The read model is repaired by that return —
a replay is the repair of last resort, not the only one. Propagating the failure guarantees that it
is recorded and that the bundle is never counted as applied.

A projection that silently skipped a failed event would leave a read model permanently missing that
event, with nothing recording which one — a corruption that only a full replay could repair and
nothing would reveal.

#### Scenario: A projection handler fails

- **WHEN** a projection handler throws while processing a bundle
- **THEN** the failure propagates out of bundle processing and is recorded
- **AND** the bundle is not treated as processed

#### Scenario: A projection handler keeps failing

- **WHEN** a projection handler throws on every delivery of a bundle
- **THEN** the bundle ends on the projection subscription's dead-letter destination, and the read
  model receives it when the operator returns it

#### Scenario: A bundle contains no events

- **WHEN** an empty bundle arrives
- **THEN** processing completes without invoking anything

### Requirement: Projections are discovered by assembly

Every concrete projection in a nominated assembly SHALL be registered, scoped to the unit of work
that processes a bundle. Abstract types and interfaces SHALL be skipped.

#### Scenario: An assembly is nominated

- **WHEN** a consumer nominates an assembly containing projections
- **THEN** every concrete projection in it is registered, and abstract types and interfaces are not

### Requirement: A replay is requested, not scheduled

A replay SHALL run only when explicitly requested. A host running the replay worker SHALL NOT begin
one on start-up.

#### Scenario: A host starts with the replay worker registered

- **WHEN** a host containing the replay worker starts and nothing requests a replay
- **THEN** the worker subscribes for requests and no replay runs

#### Scenario: A replay is requested

- **WHEN** a replay is requested
- **THEN** the worker begins one

### Requirement: A replay truncates every read model before rebuilding

A replay SHALL mark itself active, empty every registered read model, then replay the whole event
stream from the beginning in batches, and mark itself inactive when it finishes — whether it
succeeded or not.

The active marking SHALL be held for a bounded period that the replaying host renews while it works,
so that a replay whose host stops without marking itself inactive ceases to be marked active without
operator intervention. The period SHALL be configurable and SHALL default to a value that outlasts a
slow batch, because a marking that lapses while its replay is still running would let suppressed
publication resume mid-rebuild.

Truncation is what makes a replay a rebuild rather than a re-application: without it, events would
be applied a second time on top of state that already reflects them.

On the Orleans execution model a projection that declares how to empty its own read model MAY be
rebuilt alone: its checkpoints are reset, its read model emptied, and its partitions re-read from the
beginning in parallel, while every other projection keeps applying live events; a rebuild that fails
part-way resumes from the beginning. A projection that does not declare it is rebuilt with the others
by the replay, as before, and on a host whose projections read the store a full replay SHALL also
return their checkpoints to the beginning, so that they re-read what the replay emptied.

#### Scenario: A replay runs to completion

- **WHEN** a replay runs over a non-empty stream
- **THEN** it activates, truncates every read model, replays the stream in batches, records how many
  events it replayed, and deactivates

#### Scenario: The stream is empty

- **WHEN** a replay runs over an empty stream
- **THEN** it still truncates the read models and still deactivates — a rebuild from nothing produces
  nothing, not the previous contents

#### Scenario: A replay fails partway

- **WHEN** a replay fails after truncating
- **THEN** it deactivates regardless, and the read models are left in whatever partial state the
  replay reached

#### Scenario: A replay's host stops without deactivating

- **WHEN** the host running a replay stops without the replay marking itself inactive
- **THEN** the active marking lapses once it is no longer renewed, and publication is no longer
  suppressed

#### Scenario: A replay is still working

- **WHEN** a replay is between batches and has not finished
- **THEN** the active marking is renewed, so it does not lapse while the replay is still running

#### Scenario: One projection is rebuilt on the Orleans execution model

- **WHEN** a rebuildable projection is asked to rebuild while others are registered
- **THEN** only its read model is emptied and re-read, and the others apply live events throughout —
  verified with a hundred thousand events over three projections on the PostgreSQL store

#### Scenario: A full replay runs on a host with store-reading projections

- **WHEN** a full replay runs while projections read the store from checkpoints
- **THEN** their read models are emptied with the others and refilled from the beginning of the store,
  and no checkpoint is left past an entry whose effect the replay removed

### Requirement: A replay retries a failing batch before it fails

A replay SHALL apply the event stream in batches, and where a batch fails — whether reading it from
the event store or applying it to the read models — the replay SHALL retry that batch a bounded
number of times, with backoff, before treating the failure as the replay's. A retried batch SHALL be
applied again from its first entry, and each retry SHALL be recorded so an operator watching the
replay can see it. Once the attempts are exhausted, the failure SHALL end the replay exactly as an
unretried failure does today.

The retry covers a failure that passes: a read-store timeout, a dropped connection, a lock held a
moment too long. It does not make a deterministic failure survivable, and it does not continue past
one: an event that cannot be applied ends the replay after the attempts, and the read models are
left as the *A replay fails partway* scenario describes. A replay is a maintenance operation; the
fallback when one cannot complete is the backup taken before it, which is the operator's, not the
framework's.

Re-applying a batch from its start relies on the guarantee projections already give: a second
application of the same event converges on the same state, because delivery is at-least-once.

#### Scenario: A batch fails once and then succeeds

- **WHEN** applying a batch fails on the first attempt and succeeds on a later one within the
  attempt limit
- **THEN** the replay continues with the next batch, the retried batch's entries have each been
  applied at least once, and the retry was recorded

#### Scenario: Reading a batch fails once and then succeeds

- **WHEN** reading a batch from the event store fails on the first attempt and succeeds on a later
  one within the attempt limit
- **THEN** the replay continues from that batch as if the read had succeeded the first time

#### Scenario: A batch fails on every attempt

- **WHEN** a batch fails on every attempt the policy allows
- **THEN** the replay fails, its failure is recorded, and it deactivates — the same ending as a
  failure that was never retried

#### Scenario: The host shuts down while a batch is being retried

- **WHEN** the host stops while the replay is between attempts
- **THEN** the replay ends without recording a failure, as it does for shutdown at any other moment

### Requirement: A replay reports progress and failure

A replay SHALL publish the total number of events to replay and how many it has processed, and SHALL
record a failure message when it fails, so an operator can distinguish "still running" from "stopped
part way".

#### Scenario: A replay is running

- **WHEN** a replay is in progress
- **THEN** its processed count and total are readable, and a completion percentage is derivable
- **AND** a total of zero yields a defined percentage rather than a division failure

#### Scenario: A replay fails

- **WHEN** a replay fails
- **THEN** the failure message is recorded, the active flag is cleared, and a very long message is
  truncated rather than stored whole

#### Scenario: The host shuts down during a replay

- **WHEN** a replay is interrupted by host shutdown
- **THEN** it is not recorded as a failure — shutdown is not a replay error

### Requirement: A replayed event is applied under the session that produced it

While replaying, each event SHALL be processed under the session context recorded with it, not under
the replaying host's own.

Otherwise a rebuilt read model would attribute every row to whichever session happened to be
ambient, and tenant-scoped writes would land in the wrong tenant.

#### Scenario: Events from several tenants are replayed

- **WHEN** a replay processes events recorded by different sessions
- **THEN** each is processed under its own recorded session context

### Requirement: Publication is suppressed while a replay is active

While a replay is active, the framework SHALL suppress publication of anything the replayed events
provoke, so that historical events do not re-trigger side effects.

The suppression SHALL reach every host that shares the replay coordination state. Where a host holds
that state in process, the suppression reaches that host only; a deployment of several hosts that
needs a replay to suppress publication in all of them must register a shared coordination store.

#### Scenario: A replay provokes a dispatch

- **WHEN** a replayed event causes a command or bundle to be dispatched
- **THEN** it is not published to the bus while the replay is active

#### Scenario: Several hosts share the coordination state

- **WHEN** a replay is active in one host and another host that shares the coordination state
  dispatches a command or bundle
- **THEN** the other host's publication to the bus is suppressed as well — the dispatch itself
  still completes into durable storage

#### Scenario: A host holds the coordination state in process

- **WHEN** a replay is active in a host that holds the state in process and another host dispatches
  a command or bundle
- **THEN** the other host publishes as usual — it never learned of the replay, which is what the
  start-up warning of the first host said would happen

### Requirement: Replay coordination does not require shared infrastructure in a single-process host

The framework SHALL hold the replay coordination state — the active marking, the progress counters,
the failure message and the replay-request channel — in process where the host registers no shared
coordination store, and in that shared coordination store where it registers one. A host SHALL start and dispatch
in either case; the absence of a shared coordination store SHALL NOT be a start-up or first-dispatch
failure.

A host that holds the state in process SHALL record, once at start-up, that replay coordination is
confined to that process, so that an operator running several hosts learns it from the log rather
than from a side effect that was not suppressed.

#### Scenario: A host registers no shared coordination store

- **WHEN** a host composes any role that carries a dispatcher and registers no shared coordination
  store
- **THEN** the host starts, commands and bundles are dispatched, and a replay requested in that host
  runs in that host with its progress readable there

#### Scenario: A host registers a shared coordination store

- **WHEN** a host registers a shared coordination store before or after composing its role
- **THEN** the replay state is held in that shared coordination store, and a replay requested in one
  host is seen as
  active by every host sharing it, exactly as before this requirement existed

#### Scenario: A host falls back to in-process coordination

- **WHEN** a host holds the replay state in process
- **THEN** it records a warning once, at start-up, saying that replay coordination is per process
  and naming what registers the shared coordination store

### Requirement: Read models are queried through a scoped unit of work

Read-side access SHALL be through a unit of work distinct from the write side's, so that a query
never participates in a write transaction and a read model can live in its own store.

Registering the read store's database context through the framework's registration SHALL make that
read-side unit of work available without a further registration by the consumer. A read-side unit
of work the consumer registers itself SHALL take precedence over the framework's.

#### Scenario: A projection writes to a read model

- **WHEN** a projection processes a bundle
- **THEN** it does so through the read-side unit of work, in its own transaction

#### Scenario: A consumer registers only the read context

- **WHEN** a consumer registers its read-store context through the framework's registration and
  nothing else
- **THEN** the read-side unit of work resolves, bound to that context

#### Scenario: A consumer supplies its own read-side unit of work

- **WHEN** a consumer registers its own read-side unit of work, before or after registering the
  context
- **THEN** the consumer's unit of work is the one resolved

### Requirement: A projection can apply an event idempotently without masking real conflicts

The framework SHALL offer a way for a projection to apply an event whose effect may already be
present, without failing, and without suppressing a conflict that indicates genuinely concurrent
modification.

The distinction is the whole point: at-least-once delivery means a projection will see the same
event twice, and cascading deletes mean a row may vanish between the read and the write. Neither is
an error. A second writer changing a row that still exists is.

#### Scenario: The event's effect is already present

- **WHEN** a projection applies an event whose effect the read model already reflects
- **THEN** nothing is written, and the bundle continues

#### Scenario: The target of an update no longer exists

- **WHEN** a projection applies an update for a row that has since been deleted
- **THEN** the update is skipped rather than failing — the row's absence is the end state, not a
  fault

#### Scenario: A deletion races another deletion

- **WHEN** a projection deletes a row that a concurrent bundle has already deleted
- **THEN** the deletion is treated as satisfied, because the intended end state has been reached

#### Scenario: A genuine conflict occurs

- **WHEN** a projection's write conflicts with a concurrent modification to a row that still exists
- **THEN** the conflict is **not** suppressed and the bundle fails, as an unhandled projection
  failure does

### Requirement: Bundles about one aggregate are applied one at a time within a process

Where the projection worker processes bundles in parallel, it SHALL ensure that two bundles whose
events belong to the same aggregate stream are not applied concurrently within one process, while
bundles about different aggregates continue to be applied in parallel.

This is the same guarantee the command side gives for commands naming one aggregate. Without it, a
follow-up fact handed to one consumer can be applied before the fact that created the entity, which
is still in flight on another. The guarantee is per process: two processes consuming the same
subscription do not serialise against each other.

On the Orleans execution model the guarantee holds across the cluster: one reader per projection
and partition applies one batch at a time, so two facts about one aggregate never apply
concurrently anywhere.

#### Scenario: Two bundles about the same aggregate arrive concurrently

- **WHEN** two bundles whose events belong to one aggregate stream are handed to two parallel
  consumers of the same process at once
- **THEN** the second is applied only after the first has completed

#### Scenario: Two bundles about different aggregates arrive concurrently

- **WHEN** two bundles whose events belong to different aggregate streams are handed to two parallel
  consumers of the same process at once
- **THEN** both are applied in parallel

#### Scenario: A bundle spans more than one aggregate

- **WHEN** a bundle carries events from more than one aggregate stream
- **THEN** it is applied concurrently with no other bundle about any of those streams, and two such
  bundles cannot wait on each other indefinitely

#### Scenario: The number of aggregates exceeds the number of locks

- **WHEN** more distinct aggregates are in flight than the framework holds locks for
- **THEN** correctness is preserved — two unrelated aggregates may serialise against each other, but
  two bundles about the same aggregate never apply concurrently

#### Scenario: Two hosts apply facts about one aggregate on the Orleans execution model

- **WHEN** two silos run the projection readers and facts about one aggregate are committed
- **THEN** they are applied by one reader, one batch at a time, never concurrently

### Requirement: A projection can report that a fact's prerequisite has not been applied yet

The framework SHALL offer a projection a way to say that the entity a fact refers to does not exist
in its read model yet, distinct from any other failure. A bundle reported that way SHALL be retried
within the process a bounded number of times with a short backoff, holding no aggregate lock while it
waits, and SHALL fail as an unhandled projection failure only once those retries are exhausted.

The distinction is the point: "the beginning has not arrived" and "the beginning will never arrive"
produce the same observation in a projection, and only time tells them apart. A short wait resolves
the first without turning the second into a poison message.

#### Scenario: The prerequisite arrives during the wait

- **WHEN** a projection reports a missing prerequisite, and the fact that creates the entity is
  applied before the retries are exhausted
- **THEN** the bundle is applied on a later attempt and treated as processed

#### Scenario: The prerequisite never arrives

- **WHEN** a projection reports a missing prerequisite on every attempt
- **THEN** the bundle fails as an unhandled projection failure does, and the failure is recorded with
  the stream and the event type the projection named

#### Scenario: A projection fails for any other reason

- **WHEN** a projection throws anything other than the missing-prerequisite report
- **THEN** the bundle is not retried and fails on the first occurrence, as before

#### Scenario: A waiting bundle does not block the fact it waits for

- **WHEN** a bundle reports a missing prerequisite and the creating fact for the same aggregate is
  handed to another consumer of the same process
- **THEN** the creating fact is applied while the first bundle waits, rather than waiting behind it

### Requirement: The projection worker's degree of parallelism is configurable

The number of parallel consumers the projection worker opens SHALL be configurable in the
projection options. Where the configured value is not a positive number, the worker SHALL fall back
to the processor count rather than to zero.

#### Scenario: A host configures one consumer

- **WHEN** the projection options set the degree of parallelism to one
- **THEN** the worker opens a single consumer, and bundles are applied in the order the transport
  delivers them

#### Scenario: A host configures nothing

- **WHEN** the projection options do not set a degree of parallelism
- **THEN** the worker opens one consumer per processor, as it did before the option existed

#### Scenario: A host configures an invalid value

- **WHEN** the projection options set the degree of parallelism to zero or a negative number
- **THEN** the worker falls back to the processor count rather than opening no consumer

### Requirement: A projection can read the store from a checkpoint instead of consuming the bus

On the Orleans execution model a projection SHALL be fed from the event store in commit order from
a checkpoint per partition, under the session recorded with each entry, with a commit as the
wake-up and a poll as the safety net; the bus SHALL be optional for projections on that model, and
a host MAY keep publishing bundles so that both paths run — whichever applies a fact first wins,
idempotently. Every guarantee of this capability that speaks of a bundle SHALL hold for an entry
read from the store.

#### Scenario: A fact is committed

- **WHEN** events are committed on a host with store-reading projections
- **THEN** the projections apply them after the wake-up, within the latency the push path gives —
  verified at two thousand events per run on the PostgreSQL store

#### Scenario: The wake-up is lost

- **WHEN** a commit's wake-up never reaches a reader
- **THEN** the poll applies the events at its next interval, and nothing is lost

### Requirement: A stalled partition is reported

Where a store-reading projection cannot apply an entry and the checkpoint of that partition stops
advancing, the framework SHALL log the entry it stopped at and count the stall, so that an operator
sees a partition that is not moving without inspecting checkpoints.

#### Scenario: An entry keeps failing

- **WHEN** an entry fails on every retry
- **THEN** each retry is logged with the projection, the partition and the entry's identity, and the
  stall counter for that projection and partition rises
