## MODIFIED Requirements

### Requirement: Sagas consume the event stream through their own subscription

Saga processing SHALL subscribe to the event-bundle topic under a subscription of its own, separate
from the projection side's, so that each receives every bundle independently. On the Orleans
execution model sagas SHALL instead be fed from the event store in commit order from a checkpoint of
their own per partition, independent of the projections' checkpoints, so that each side still
receives every fact whether or not the other is deployed.

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

## ADDED Requirements

### Requirement: A saga can be a stateful process with a correlation and a timeout

On the Orleans execution model a saga MAY opt in to state: it declares which events it handles and
how it correlates them, and the framework SHALL keep its state per correlation in an event stream of
its own — never in grain storage — SHALL rehydrate it from that stream, and SHALL let it register a
timeout that fires once per cluster on or after its due time and survives a restart. A saga that
does not opt in SHALL keep running unchanged and stateless, as the contract says.

#### Scenario: A process times out after a restart

- **WHEN** a process registers a timeout and the silo is killed before it is due
- **THEN** the timeout fires after the restart, once, and the process handles it with its
  rehydrated state — verified with three kills on the PostgreSQL store

#### Scenario: A stateless saga is registered beside a process

- **WHEN** a host registers an existing stateless saga and a stateful process
- **THEN** the stateless saga behaves as it did on the bus, one instance per fact, no state kept
