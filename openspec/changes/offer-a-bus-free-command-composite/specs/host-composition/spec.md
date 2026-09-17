## MODIFIED Requirements

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
