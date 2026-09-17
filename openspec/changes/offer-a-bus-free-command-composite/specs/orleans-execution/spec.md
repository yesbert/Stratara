## MODIFIED Requirements

### Requirement: The execution model can be adopted per role beside the bus workers

Each role — commands, projections, sagas, outbox drain, timers, heavy work — SHALL be adopted with
one registration after the role's existing composite, and a host SHALL be able to run both models
at once during a rollout, because both apply idempotently. A role's work SHALL run only on a silo
that registered the role: a cluster whose silos register different roles SHALL place each
aggregate, projection, saga, timer owner and heavy unit on a silo that registered its role, and a
call for a role no silo of the cluster registered SHALL fail with a message naming the role rather
than activate where the role is missing. A host that only dispatches commands MAY join the cluster
as a client rather than a silo. A host that registers the execution model
without the storage-backed grain directory it requires SHALL fail at start with a message naming
what is missing, not at the first activation, and so SHALL a host with an invalid setting, naming the
setting. The host's own timer owners and handlers SHALL be honoured whatever order they are registered
in relative to the execution model.

A message broker SHALL NOT be a runtime dependency of a host whose command dispatcher and bundle
dispatcher the execution model has both replaced: a silo composed with the command services
composite, the execution model's command dispatcher with an intent store, the aggregate grains and
at least one store-reading role, and a host that only dispatches through the execution model's
dispatcher, SHALL start, run commands, commit facts and apply them with no broker configured and SHALL
open no broker connection. A silo that commits facts without a store-reading role SHALL keep
publishing bundles to the bus, because it has no reader to wake, and the documentation SHALL say so.
The documentation SHALL name the command composite without the bus-fed worker, SHALL state that it
replaces the worker composite once no consumer of the deployment reads bundles from the bus, and
SHALL say what becomes of the bus queues after the cut-over: which queues the bus workers own, that
every publisher to an exchange is retired before its queues are deleted, that a dead-letter queue is
emptied deliberately, and that a publication kept after its queues are deleted stores every bundle for
a drain that cannot deliver it.

#### Scenario: A host adopts the projection role

- **WHEN** a host calls the projection services composite and then the execution model's projection
  registration
- **THEN** the bus-fed projection worker is not registered and the store-reading projections are

#### Scenario: Silos register different roles

- **WHEN** one silo of a cluster registers the command role and another the projection, saga and
  timer roles, and commands are dispatched, facts committed and timers registered
- **THEN** every command runs on the command silo, every projection, saga and timer owner runs on the
  other, and no call fails for a role missing on the silo it ran on

#### Scenario: No silo registers a role

- **WHEN** a command is dispatched to a cluster in which no silo registered the command role
- **THEN** the dispatch fails with a message naming the command role, and no activation is left
  behind on a silo that lacks it

#### Scenario: The API host joins as a client

- **WHEN** a host registers the execution model's command dispatcher and joins the cluster as an
  Orleans client, not a silo
- **THEN** its dispatches are recorded and run on a silo that registered the command role

#### Scenario: A host forgets the directory

- **WHEN** a host registers the execution model but no storage-backed grain directory under the
  name the model selects
- **THEN** the silo fails to start with a message that names the missing registration

#### Scenario: A host configures an invalid setting

- **WHEN** a host configures a non-positive batch size, limit or partition count, or a period below the
  runtime's minimum
- **THEN** the host fails at start with a message that names the setting

#### Scenario: A host registers its timer owners after the execution model

- **WHEN** a host registers its own timer owners and handlers after the execution model's registrations
- **THEN** its timers fire for its owners, and stateful processes' timeouts still fire

#### Scenario: A command silo runs without a broker

- **WHEN** a silo is composed with the command services composite, the execution model's command
  dispatcher and intent store, the aggregate grains and the projection role, a client host with the
  dispatcher joins it, and neither has a broker configured
- **THEN** both start, a command dispatched from the client runs in its aggregate's activation on the
  silo, the facts it commits are applied by the projection, no bundle is stored in the outbox, and no
  broker connection is attempted — verified on the PostgreSQL store

#### Scenario: A silo commits without a store-reading role

- **WHEN** a silo registers the command role and no projection or saga role, and a handler on it
  commits facts
- **THEN** the bundle is published to the bus as before, and the documentation states that such a
  silo keeps the broker until it registers a store-reading role
