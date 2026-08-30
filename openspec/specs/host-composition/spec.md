# host-composition Specification

## Purpose
Let each process in a deployment take exactly the parts of the framework its role needs — one line
per role — so that a projection worker does not carry command handling, and a web host does not
carry a message-bus consumer it will never use.

## Requirements

### Requirement: Each worker role has one composite that wires it

The framework SHALL offer one composition entry point per worker role — backend services, command
handling, heavy command handling, event projection, saga orchestration, event-stream hashing and
outbox handling — so that a host opts into a role with a single call.

#### Scenario: A host adopts one role

- **WHEN** a host calls the composite for a role
- **THEN** the components that role needs are registered, including its hosted worker where the role
  has one

#### Scenario: A host adopts several roles

- **WHEN** a host calls more than one composite
- **THEN** each role's components are registered and the shared base is not duplicated

### Requirement: Every composite applies the same common base

Every worker composite SHALL apply one shared set of cross-cutting services — messaging, identity,
session context, security, event mapping and resilience policies — so that no role can be composed
without the services every role assumes are present.

#### Scenario: Any composite is used

- **WHEN** a host calls any of the worker composites
- **THEN** the common cross-cutting services are registered, without the host naming them

#### Scenario: A host composes its own role

- **WHEN** a host needs a combination the composites do not offer
- **THEN** it can apply the common base directly and add the parts it needs

### Requirement: Handlers and components are discovered by assembly

The framework SHALL offer discovery that registers every command handler, query handler, projection
and saga in a nominated assembly, so that adding one does not require editing composition code.

#### Scenario: An assembly is nominated for discovery

- **WHEN** a host nominates an assembly for a component kind
- **THEN** every concrete implementation of that kind in the assembly is registered against the
  contracts it implements

### Requirement: Work can be queued for in-process execution

The framework SHALL offer a bounded in-process queue for work that should happen after the current
request returns, executed by a hosted service, with each item running in its own dependency
injection scope.

#### Scenario: Work is queued

- **WHEN** a caller queues a unit of work
- **THEN** it receives an identifier for it, and the work executes later on a background worker

#### Scenario: The queue is full

- **WHEN** the queue is at capacity
- **THEN** queuing waits for space rather than discarding the work or growing without bound

#### Scenario: Work is executed

- **WHEN** a queued unit of work runs
- **THEN** it runs inside a fresh dependency-injection scope, so it may resolve scoped services
  without borrowing the originating request's

### Requirement: Queued work reports its own outcome

The queue SHALL track each unit of work's status through queued, running and either completed or
failed, and SHALL make that status retrievable by the identifier the caller received. A failure
SHALL be recorded against the item and SHALL NOT stop the background worker.

#### Scenario: Work completes

- **WHEN** a queued unit of work returns normally
- **THEN** its status reads as completed

#### Scenario: Work fails

- **WHEN** a queued unit of work throws
- **THEN** its status reads as failed and carries the failure message
- **AND** the background worker continues processing subsequent items

#### Scenario: An unknown identifier is queried

- **WHEN** status is requested for an identifier the queue does not know
- **THEN** no status is returned, rather than a fabricated one

### Requirement: Status retention is bounded

The queue SHALL retain a bounded number of status records, discarding the oldest first, so that a
long-running host's memory does not grow with the number of items it has ever queued.

#### Scenario: The retention limit is exceeded

- **WHEN** more items are queued than the retention limit
- **THEN** the oldest status records are discarded and the most recent are retained

#### Scenario: The retention limit is not reached

- **WHEN** fewer items are queued than the retention limit
- **THEN** every status record is retained

### Requirement: Background execution is parallel and ordered on entry

The queue SHALL preserve the order in which work was queued as the order in which it is taken up,
and SHALL execute items across several workers rather than one at a time.

#### Scenario: Several items are queued

- **WHEN** several units of work are queued in sequence
- **THEN** they are taken up in that order

#### Scenario: The host is stopping

- **WHEN** the host is shutting down
- **THEN** the background worker stops taking new work and the shutdown is recorded

### Requirement: Framework failures can be answered as a standard HTTP problem response

A host SHALL be able to opt into mapping the framework's own failure types to a standard
machine-readable problem response, so that a caller receives the same shape for every framework
rejection rather than one shape per failure type.

Mapping SHALL be opt-in, and SHALL leave any failure the framework did not raise untouched — a host
with its own error model must be able to keep it.

#### Scenario: A request fails validation

- **WHEN** a request is rejected by validation
- **THEN** the response carries a client-error status and the failures, grouped so a caller can
  attribute each to the field it concerns

#### Scenario: A request is refused by authorization or tenant isolation

- **WHEN** a request is refused for a missing role, a missing permission or a tenant-access denial
- **THEN** the response carries a forbidden status in the same problem shape

#### Scenario: A failure the framework did not raise

- **WHEN** any other failure reaches the boundary
- **THEN** it is not converted, and propagates unchanged

#### Scenario: The host does not opt in

- **WHEN** a host does not register the mapping
- **THEN** the framework converts nothing, and the host's own error handling applies
