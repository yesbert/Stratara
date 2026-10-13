## ADDED Requirements

### Requirement: A command's named owner holds on the execution model's path

A command that names the owner of its protected fields SHALL be treated on the execution model's path
as it is on the bus path: its durable record and what its activation receives SHALL be written for the
named owner, the activation SHALL read its protected fields for that owner, and the handler SHALL run
under the session recorded with the command. This SHALL hold for a command handed to its activation by
the dispatch that recorded it, for one forwarded to its activation without a record, and for one
resumed from its record after a crash.

A resumed command's owner SHALL be taken from its signed record and from nothing beside it, as its
routing is. A command that names no owner SHALL be recorded and handed over as before a command could
name one.

A cluster SHALL be upgraded as a fleet on the bus is: a silo on a release that cannot read the owner
fails such a command rather than running its handler with the fields emptied, so every silo takes the
release before any host dispatches a command that names an owner.

#### Scenario: A command naming an owner is recorded and run

- **WHEN** a command naming a tenant as its owner is dispatched through the recording dispatcher from
  a session without a tenant
- **THEN** its record holds its protected fields written for the named tenant, and its handler
  receives their values and runs under the dispatching session

#### Scenario: A command naming an owner is forwarded to its activation

- **WHEN** a command naming an owner is forwarded to the activation of the aggregate it names without
  a record of its own
- **THEN** the activation reads its protected fields for the named owner

#### Scenario: A command naming an owner is resumed after a crash

- **WHEN** a host dies after recording a command that names an owner and before its handler ran
- **THEN** the resumed command is read for the owner its record names and its handler receives the
  protected fields — verified on the PostgreSQL store

#### Scenario: A command names no owner

- **WHEN** a command that names no owner is dispatched on this path
- **THEN** it is recorded, handed over and read as before a command could name one
