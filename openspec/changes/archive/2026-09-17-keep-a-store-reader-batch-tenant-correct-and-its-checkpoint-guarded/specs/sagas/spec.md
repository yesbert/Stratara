## MODIFIED Requirements

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
