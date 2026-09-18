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

For command handling, event projection and saga orchestration, whose composites register the role's
services together with its bus-fed worker, the framework SHALL also offer the same composite without the
worker, so that a host adopting the Orleans execution model registers the role's services and then
the execution model's registration for that role, and nothing is removed after it was registered. The
command composite without the worker SHALL keep the dispatchers the worker composite registers, so
that the execution model's dispatcher replaces the bus dispatcher after it as it does after the other
composites. A host on the projection services composite SHALL still be able to run a full replay. The
existing composites SHALL keep registering what they register today.

#### Scenario: A host adopts one role

- **WHEN** a host calls the composite for a role
- **THEN** the components that role needs are registered, including its hosted worker where the role
  has one

#### Scenario: A host adopts several roles

- **WHEN** a host calls more than one composite
- **THEN** each role's components are registered and the shared base is not duplicated

#### Scenario: A host adopts a role on the Orleans execution model

- **WHEN** a host calls the role's services composite and then the execution model's registration
  for that role
- **THEN** the role's services and the execution model's readers are registered and no bus-fed
  worker is, without any registration having been removed

#### Scenario: A host composes the command role without the bus-fed worker

- **WHEN** a host calls the command services composite
- **THEN** the mediator, the write store, event sourcing and the outbox dispatchers are registered,
  no hosted service consumes a command topic, and a host that then registers the execution model's
  command dispatcher and aggregate grains has replaced the bus dispatcher without removing anything
  itself

#### Scenario: A host registers the silo and the composites in either order

- **WHEN** a host registers the Orleans silo before or after the framework's composites
- **THEN** the host starts and both work — verified in both orders

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
with its own error model must be able to keep it. A request that fails because it carries no identity —
an unauthenticated caller reaching an operation that needs a session — SHALL be answered as
unauthenticated, through the host's authentication challenge where it has one, and never as a server
error; the same failure for a caller that is authenticated means the host did not establish the
session context and SHALL NOT be converted, so that the misconfiguration stays loud.

#### Scenario: A request fails validation

- **WHEN** a request is rejected by validation
- **THEN** the response carries a client-error status and the failures, grouped so a caller can
  attribute each to the field it concerns

#### Scenario: A request is refused by authorization or tenant isolation

- **WHEN** an authenticated caller's request is refused for a missing role, a missing permission or a
  tenant-access denial
- **THEN** the response carries a forbidden status in the same problem shape

#### Scenario: A failure the framework did not raise

- **WHEN** any other failure reaches the boundary
- **THEN** it is not converted, and propagates unchanged

#### Scenario: The host does not opt in

- **WHEN** a host does not register the mapping
- **THEN** the framework converts nothing, and the host's own error handling applies

#### Scenario: An unauthenticated caller reaches an operation that needs a session

- **WHEN** a caller that is not authenticated sends a request that saves events or dispatches a command,
  and no guard refused it first
- **THEN** the response carries the unauthenticated status — through the host's challenge where an
  authentication scheme is registered, in the problem shape otherwise — and not a server error

#### Scenario: An authenticated caller has no session context

- **WHEN** an authenticated caller's request fails because no session context was established
- **THEN** the failure is not converted, and propagates unchanged

### Requirement: A setting that names its configuration section is read from it

Every framework setting whose documentation names a configuration section SHALL be read from that
section of the host's configuration by the registration that adds it, whether the host calls that
registration directly or through a composite, so that a value written in the named section takes
effect without code. Where the host's services carry no configuration, the registration SHALL still
succeed with the defaults. A value the host configures in code after the registration SHALL take
precedence over the section. A setting whose value cannot work SHALL be refused when the host starts,
with a message naming the setting, rather than accepted and failing later; the projection replay's
lease period SHALL be refused at zero or below.

#### Scenario: The session context is configured in the application settings

- **WHEN** a host sets the tenant-header opt-in in the session-context section of its configuration
  and registers the session context
- **THEN** the opt-in is in effect, without a line of code configuring it

#### Scenario: The replay lease is configured in the application settings

- **WHEN** a host sets the replay lease in the projection-replay section of its configuration
- **THEN** a replay's marking lasts that long

#### Scenario: Code overrides the section

- **WHEN** a host sets a value in the section and configures the same setting in code after
  registering
- **THEN** the value from code is in effect

#### Scenario: The replay lease is zero

- **WHEN** a host configures a replay lease of zero seconds
- **THEN** the host fails to start with a message naming the setting

#### Scenario: No configuration is registered

- **WHEN** the session context or the replay state is registered on a service collection that carries
  no configuration
- **THEN** the registration succeeds and the defaults apply
