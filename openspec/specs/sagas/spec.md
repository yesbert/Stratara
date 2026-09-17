# sagas Specification

## Purpose
Let a process that spans several aggregates react to what happened — issuing the next command when
an event arrives — without any aggregate knowing that process exists.

## Requirements

### Requirement: A saga declares the events it reacts to by handling them

A saga SHALL receive only the events it declares handlers for, determined from its handler
signatures. Handlers SHALL be found whether or not they are publicly visible, so a saga can keep its
reaction surface out of its public API.

#### Scenario: A bundle contains a mix of events

- **WHEN** a bundle contains events a saga handles and events it does not
- **THEN** only the handled ones are dispatched to it

#### Scenario: A saga handles nothing in the bundle

- **WHEN** no event in a bundle matches a saga's declared handlers
- **THEN** that saga is not dispatched at all

#### Scenario: A saga declares no handlers

- **WHEN** a saga declares no handlers
- **THEN** it reacts to nothing rather than to everything

#### Scenario: A handler is not publicly visible

- **WHEN** a saga declares a handler that is not public
- **THEN** it is still found and dispatched to

### Requirement: A handler may take the event payload or the enveloped event

A saga handler SHALL be invoked with the event payload where it declares one, and with the enveloped
event where it declares that instead.

#### Scenario: The saga declares a payload handler

- **WHEN** a relevant event arrives and the saga declares a handler taking its payload
- **THEN** that handler is invoked with the payload

#### Scenario: The saga declares only an enveloped handler

- **WHEN** no payload handler matches
- **THEN** the enveloped handler is invoked instead

### Requirement: Sagas run in parallel with each other, and in order within themselves

Every registered saga SHALL be dispatched a bundle concurrently with the others. Within one saga,
the events of a bundle SHALL be dispatched in the order they appear.

A saga must therefore not assume it is alone: two sagas reacting to the same event run at the same
time, and neither sees the other's effects.

#### Scenario: Several sagas react to one bundle

- **WHEN** a bundle arrives and several sagas find it relevant
- **THEN** they are dispatched concurrently

#### Scenario: One saga receives several events

- **WHEN** a bundle contains several events one saga handles
- **THEN** that saga receives them in the order they appear in the bundle

#### Scenario: No sagas are registered

- **WHEN** a bundle arrives and no sagas are registered
- **THEN** processing completes without error

### Requirement: Sagas consume the event stream through their own subscription

Saga processing SHALL subscribe to the event-bundle topic under a subscription of its own, separate
from the projection side's, so that each receives every bundle independently. On the Orleans
execution model sagas SHALL instead be fed from the event store in commit order from a checkpoint of
their own per partition, independent of the projections' checkpoints, so that each side still
receives every fact whether or not the other is deployed. On that model the session recorded with
an entry SHALL be in place before the sagas and what they depend on are resolved for it, so that a
saga whose dependencies take the tenant or the user when they are constructed sees the entry's,
whatever the entries around it were recorded under.

#### Scenario: A bundle is published

- **WHEN** an event bundle is published
- **THEN** both the saga side and the projection side receive it, neither consuming it from the other

#### Scenario: Only the saga worker is deployed

- **WHEN** a host runs the saga worker and no projection worker
- **THEN** sagas still receive every bundle

#### Scenario: Sagas read the store on the Orleans execution model

- **WHEN** events are committed on a host with store-reading sagas and no projection readers
- **THEN** every saga receives every fact, in commit order within a stream, under the recorded
  session — verified on the PostgreSQL store with an unchanged saga

#### Scenario: A saga's dependency takes its tenant at construction

- **WHEN** entries of two tenants are read from the store within one read, and a saga depends on a
  service that reads the ambient tenant when it is constructed
- **THEN** the service the saga receives for each entry was constructed under that entry's tenant —
  verified on the PostgreSQL store with the native reader

### Requirement: Sagas are discovered by assembly

Every concrete saga in a nominated assembly SHALL be registered, scoped to the processing of a
bundle. Abstract types and interfaces SHALL be skipped.

#### Scenario: An assembly is nominated

- **WHEN** a consumer nominates an assembly containing sagas
- **THEN** every concrete saga in it is registered, and abstract types and interfaces are not

### Requirement: Bundles arriving at sagas are verified like any other bus message

A bundle reaching the saga side SHALL be subject to the same envelope-integrity verification and the
same parsing bounds as any other consumed message.

#### Scenario: An unsigned bundle arrives in strict mode

- **WHEN** an unsigned or tampered bundle reaches the saga side and strict verification is configured
- **THEN** it is refused before any saga sees it

#### Scenario: An oversized bundle arrives

- **WHEN** a bundle exceeds the configured size limit
- **THEN** it is refused before being parsed

### Requirement: Saga processing is measured

The framework SHALL report how many bundles sagas are processing at any moment, how many events they
have processed, and how long bundle processing takes — each dimensioned by outcome.

#### Scenario: An operator watches saga load

- **WHEN** bundles are being processed
- **THEN** the in-flight count, the processed-event count and the processing duration are observable,
  with successes distinguishable from failures

### Requirement: Bundles about one aggregate reach sagas one at a time within a process

Where the saga worker processes bundles in parallel, it SHALL ensure that two bundles whose events
belong to the same aggregate stream are not dispatched to sagas concurrently within one process,
while bundles about different aggregates continue to be dispatched in parallel.

The requirement *Sagas run in parallel with each other, and in order within themselves* speaks about
the sagas inside one bundle. This one speaks about bundles across consumers: the second fact about an
aggregate does not reach a saga while the first is still being handled next door. The guarantee is
per process. On the Orleans execution model it holds across the cluster: one reader per partition
dispatches one batch at a time.

#### Scenario: Two bundles about the same aggregate arrive concurrently

- **WHEN** two bundles whose events belong to one aggregate stream are handed to two parallel
  consumers of the same process at once
- **THEN** the second is dispatched only after the first has completed

#### Scenario: Two bundles about different aggregates arrive concurrently

- **WHEN** two bundles whose events belong to different aggregate streams are handed to two parallel
  consumers of the same process at once
- **THEN** both are dispatched in parallel

#### Scenario: The number of aggregates exceeds the number of locks

- **WHEN** more distinct aggregates are in flight than the framework holds locks for
- **THEN** correctness is preserved — two unrelated aggregates may serialise against each other, but
  two bundles about the same aggregate never dispatch concurrently

#### Scenario: Two silos dispatch facts about one aggregate on the Orleans execution model

- **WHEN** two silos run the saga readers and facts about one aggregate are committed
- **THEN** the sagas receive them from one reader, one batch at a time, never concurrently

### Requirement: A saga can report that a fact's prerequisite has not been applied yet

The framework SHALL let a saga report that something the fact refers to does not exist yet, in the
same way a projection can, and SHALL retry the bundle within the process a bounded number of times
with a short backoff, holding no aggregate lock while it waits, before failing it as an unhandled
saga failure.

#### Scenario: The prerequisite arrives during the wait

- **WHEN** a saga reports a missing prerequisite, and what it waits for exists before the retries are
  exhausted
- **THEN** the bundle is dispatched on a later attempt and treated as processed

#### Scenario: The prerequisite never arrives

- **WHEN** a saga reports a missing prerequisite on every attempt
- **THEN** the bundle fails as an unhandled saga failure does, and the failure is recorded with the
  stream and the event type the saga named

#### Scenario: A saga fails for any other reason

- **WHEN** a saga throws anything other than the missing-prerequisite report
- **THEN** the bundle is not retried and fails on the first occurrence, as before

### Requirement: The saga worker's degree of parallelism is configurable

The number of parallel consumers the saga worker opens SHALL be configurable in the saga options.
Where the configured value is not a positive number, the worker SHALL fall back to the processor
count rather than to zero.

#### Scenario: A host configures one consumer

- **WHEN** the saga options set the degree of parallelism to one
- **THEN** the worker opens a single consumer, and bundles are dispatched in the order the transport
  delivers them

#### Scenario: A host configures nothing

- **WHEN** the saga options do not set a degree of parallelism
- **THEN** the worker opens one consumer per processor, as it did before the option existed

#### Scenario: A host configures an invalid value

- **WHEN** the saga options set the degree of parallelism to zero or a negative number
- **THEN** the worker falls back to the processor count rather than opening no consumer

### Requirement: A failing saga does not lose the bundle

Where a saga handler fails, the failure SHALL propagate rather than being swallowed, so the bundle
is not acknowledged on the saga subscription and is not treated as processed. The bundle SHALL then
be delivered again a bounded number of times and, after the bound, moved to the saga subscription's
dead-letter destination as `outbox-and-messaging` → *A message a handler cannot take is retried a
bounded number of times and then kept* states — never discarded.

A saga has no replay. A bundle that vanished from its subscription was a step in a process that
nobody would ever take; keeping the bundle is the only way that process can still complete.

#### Scenario: A saga handler fails

- **WHEN** a saga handler throws while processing a bundle
- **THEN** the failure propagates out of bundle processing and is recorded
- **AND** the bundle is not treated as processed and is delivered again

#### Scenario: A saga handler keeps failing

- **WHEN** a saga handler throws on every delivery of a bundle
- **THEN** the bundle ends on the saga subscription's dead-letter destination, and the saga receives
  it when the operator returns it

#### Scenario: A saga's command conflicts

- **WHEN** the command a saga issues in reaction to a bundle reports a concurrency conflict
- **THEN** the bundle is redelivered under the conflict bound rather than the failure bound

### Requirement: A saga can be a stateful process with a correlation and a timeout

On the Orleans execution model a saga MAY opt in to state: it declares which events it handles and
how it correlates them, and the framework SHALL keep its state per correlation in an event stream of
its own, so that it needs no storage of its own to configure, SHALL rehydrate it from that stream, and
SHALL let it register a timeout that fires once per cluster on or after its due time and survives a
restart. A fact MAY reach a process more than once, and a timeout MAY reach it for a step whose events
were not recorded; the framework SHALL hand every delivery the state as recorded, so that the process
decides from it. A saga that does not opt in SHALL keep running unchanged and stateless, as the
contract says. A fact SHALL reach a process under the session recorded with it, in place before the
process's state is read, so that a state stream reached through a connection routed per tenant is
read under the fact's tenant. A timeout has no fact to take a session from: it SHALL run under the
session the process's state stream was created with, which is read before that session can be in
place, and the documentation SHALL say so where a process's timeouts are described, so that a
consumer routing per tenant knows the timeout path reads the state stream under no tenant.

#### Scenario: A process times out after a restart

- **WHEN** a process registers a timeout and the silo is killed before it is due
- **THEN** the timeout fires after the restart, once, and the process handles it with its
  rehydrated state — verified with three kills on the PostgreSQL store

#### Scenario: A stateless saga is registered beside a process

- **WHEN** a host registers an existing stateless saga and a stateful process
- **THEN** the stateless saga behaves as it did on the bus, one instance per fact, no state kept

#### Scenario: A fact reaches a process twice

- **WHEN** a fact is delivered to a process again after a crash
- **THEN** the process receives it with state that already reflects its first handling, if that
  handling was recorded

#### Scenario: A fact reaches a process through a tenant-routed store

- **WHEN** a process's state stream is reached through a service that takes the tenant when it is
  constructed, and a fact of that tenant is handed to the process
- **THEN** the state is read and the emitted events are appended under the fact's tenant — verified
  on the PostgreSQL store
