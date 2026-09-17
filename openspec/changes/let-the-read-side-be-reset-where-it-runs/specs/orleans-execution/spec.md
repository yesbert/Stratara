# orleans-execution

## MODIFIED Requirements

### Requirement: Projections and sagas read the store in commit order and never miss a committed fact

Where a host has registered the Orleans execution model for projections or sagas, each SHALL read
the event store from a checkpoint, in an order in which no entry at or below a checkpoint can still
commit later, and apply what it reads under the session recorded with each entry. The recorded
session SHALL be in place before the projection or the sagas, and anything they depend on, are
resolved for the entry, so that a service that takes its tenant or its user when it is constructed
— a connection routed per tenant, a read-model context that captures the tenant — takes the entry's;
one read of the store MAY return entries recorded under several sessions, and each SHALL be applied
as it would have been had it arrived on its own. A commit SHALL
wake the readers of the partitions it touched; a wake-up that is lost costs latency and never a
fact, because a poll reads the store regardless. A committed fact SHALL reach every projection and
saga whatever dies after the commit. Two rebuilds of one projection SHALL NOT interleave: the
projection's readers SHALL resume only when every rebuild that paused them has finished, and a
rebuild requested while a full replay is active SHALL be refused with a message that says so. A full
replay on a host whose projections read the store SHALL return every such projection's checkpoint to
the beginning before the read models are emptied, and the replay's own pass over the store SHALL be
followed by the readers' pass from the beginning once the replay ends, so that every store-reading
projection applies the store twice under a full replay and is correct because it applies idempotently;
the documentation SHALL say so where it describes the full replay and SHALL name the rebuild of a
single projection as the way to re-read a read model once. A
reader brought back for a partition the host's partition count no longer has SHALL retire itself —
stop returning, log that it did, and read nothing — so that lowering the count leaves no reader that
returns every keep-alive period to be refused.

Pausing the readers for a rebuild or a replay SHALL leave none of them paused where it does not
finish: a pause that fails SHALL resume the readers that did pause and SHALL fail naming what it
could not pause, so that no partition is left waiting for a resume that never comes. Returning a
checkpoint to the beginning SHALL be accepted whatever reader last wrote it — the beginning means
the same under every reader and every partition count — so that a deployment whose reader or
partition count changed can rebuild or replay from inside the running cluster, and the documentation
SHALL name that as the way to recover from a refused checkpoint. A wake-up that cannot be sent SHALL
be logged with the consumer it was meant for, so that a wake-up path that is always lost is visible
as more than latency, and one wake-up that fails SHALL NOT keep the other consumers of that commit
from being woken.

#### Scenario: The host dies between the commit and the wake-up

- **WHEN** a host is killed after committing events and before any wake-up or publication
- **THEN** every projection and saga applies those events after the host or another silo reads the
  store — verified with twenty kills on the PostgreSQL store, none lost and, the kill falling before
  any read, none applied twice

#### Scenario: Two transactions commit out of sequence order

- **WHEN** a transaction with the lower sequence number commits after one with a higher one
- **THEN** no reader skips the late committer — verified on the PostgreSQL store with the native
  reader and on any relational store with the portable reader

#### Scenario: A projection is rebuilt

- **WHEN** a rebuildable projection is asked to rebuild
- **THEN** only its read model is emptied and re-read from the beginning of the store, in parallel
  over the partitions, while every other projection keeps applying live events

#### Scenario: A rebuild fails part-way

- **WHEN** emptying a rebuildable projection's read model fails part-way through a rebuild
- **THEN** the projection re-reads from the beginning of the store when it resumes, and never applies
  on top of a partly emptied read model without re-reading

#### Scenario: A projection is asked to rebuild twice at once

- **WHEN** a second rebuild of a projection is requested while the first is still emptying its read
  model
- **THEN** the projection's readers do not resume until both have finished, and the read model holds
  every fact of the store once the readers have caught up

#### Scenario: A reader cannot be paused for a rebuild

- **WHEN** one partition's reader cannot be paused for a rebuild
- **THEN** the rebuild fails naming that partition, and no reader it had paused stays paused

#### Scenario: A read model is rebuilt after the partition count changed

- **WHEN** a read model is rebuilt on a host whose checkpoints were written by another reader or under
  another partition count
- **THEN** the rebuild returns them to the beginning under the host's own reader and the read model is
  re-read, without stopping the deployment

#### Scenario: A wake-up cannot be sent

- **WHEN** a commit's wake-up cannot be delivered for one consumer
- **THEN** it is logged naming that consumer, and the commit's other consumers are woken all the same

#### Scenario: A rebuild is requested during a full replay

- **WHEN** a projection's rebuild is requested while a full replay is active
- **THEN** the rebuild is refused with a message naming the replay, and the replay is not disturbed

#### Scenario: A full replay runs on a host whose projections read the store

- **WHEN** a full replay is requested on a host whose projections read the store
- **THEN** every store-reading projection's checkpoint is at the beginning when its read model is
  emptied, its read model holds every fact once the readers have caught up after the replay, and a
  projection that counts its applications has applied each fact twice — verified on the PostgreSQL
  store with the native reader

#### Scenario: One read returns entries of several tenants

- **WHEN** a partition holds entries recorded under two tenants within one read, and a projection or
  saga depends on a service that takes the tenant when it is constructed
- **THEN** each entry is applied with that service constructed under the entry's own tenant, and none
  under the other's or under no tenant — verified on the PostgreSQL store with the native reader, for
  a projection and for a saga

#### Scenario: The partition count is lowered under the native reader

- **WHEN** a host that reads with the native reader lowers its partition count and its readers'
  checkpoints are reset as documented, and the keep-alive of a reader beyond the new count brings it
  back
- **THEN** that reader stops returning, reads nothing, logs that it retired, and no stall is counted
  for it — verified on the PostgreSQL store

### Requirement: The execution model can be adopted per role beside the bus workers

Each role — commands, projections, sagas, outbox drain, timers, heavy work — SHALL be adopted with
one registration after the role's existing composite, and a host SHALL be able to run both models
at once during a rollout, because both apply idempotently. A host that asks the projection or saga
registration to keep publishing bundles to the bus SHALL keep the bus dispatcher it registered
whatever the shape of that registration, and a host that asks for it without having registered a
bus dispatcher SHALL fail at registration with a message naming what is missing rather than run
without publishing. This SHALL hold for every registration that asks for it, whether it is the first
store-reading role the host registers or a later one, so that asking and being silently ignored is
impossible. Each of the model's registrations SHALL
be idempotent in itself: called twice, whether by the host or by a composite of the host's that
wraps it, it SHALL leave the composition as one call leaves it, so that no projection is woken twice,
no consumer is listed twice, and no work is started twice. A role's work SHALL run only on a silo
that registered the role: a cluster whose silos register different roles SHALL place each
aggregate, projection, saga, timer owner and heavy unit on a silo that registered its role, and a
call for a role no silo of the cluster registered SHALL fail with a message naming the role rather
than activate where the role is missing. A host that only dispatches commands MAY join the cluster
as a client rather than a silo. A host that registers the execution model
without the storage-backed grain directory it requires SHALL fail at start with a message naming
what is missing, not at the first activation, and so SHALL a host with an invalid setting, naming the
setting. A silo that registers a role or a singleton work without publishing it to the cluster —
because it registered the directory itself rather than through the call that publishes — SHALL fail
at start with a message naming the roles and works it found and the call that publishes them, rather
than start and be placed on as if it hosted everything. The host's own timer owners and handlers
SHALL be honoured whatever order they are registered
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

The documentation SHALL show how a consumer tests its handlers, projections, sagas and timers on the
execution model in one process, with the registrations it uses in production, and SHALL ship a runnable
sample that does so.

#### Scenario: A host adopts the projection role

- **WHEN** a host calls the projection services composite and then the execution model's projection
  registration
- **THEN** the bus-fed projection worker is not registered and the store-reading projections are

#### Scenario: A host keeps the bus beside the grains with a dispatcher registered by a factory

- **WHEN** a host registers its bus bundle dispatcher through a factory or an instance and then asks
  the projection registration to keep publishing to the bus
- **THEN** a committed bundle still reaches the bus dispatcher, and the store-reading grains are woken

#### Scenario: A second store-reading role asks to keep the bus

- **WHEN** a host registers one store-reading role without keeping the bus and then a second one that
  asks to keep it
- **THEN** either the bundles still reach the bus dispatcher, or the registration fails naming what is
  missing — the request is not ignored

#### Scenario: A host asks to keep the bus without a bus dispatcher

- **WHEN** a host asks the projection or saga registration to keep publishing to the bus while no bus
  bundle dispatcher is registered
- **THEN** the registration fails with a message naming the parameter and the registration that
  supplies a bus dispatcher

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

#### Scenario: A registration is called twice

- **WHEN** a host calls any of the model's registrations twice — the aggregate, projection, saga,
  timer, dispatcher, heavy-work or singleton-work registration
- **THEN** the composition is the one a single call leaves: each projection is woken once per bundle,
  the seeding and the reset list each consumer once, and each work runs once per period

#### Scenario: A silo registers the directory itself and hosts a role

- **WHEN** a silo registers a grain directory under the model's name directly, not through the call
  that publishes, and registers a role or a singleton work
- **THEN** the silo fails at start with a message naming the roles and works it registered and the
  call that publishes them, and a silo that registers neither a role nor a work starts

#### Scenario: A silo registers the directory itself and hosts nothing

- **WHEN** a silo registers a grain directory under the model's name directly and registers only the
  command dispatcher
- **THEN** it starts, because it hosts nothing that is placed by role

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

#### Scenario: A consumer tests on the execution model

- **WHEN** a consumer follows the testing documentation for the execution model
- **THEN** its handlers, projections, sagas and timers run through the registrations it uses in
  production, in the test's process, without a cluster, a broker or a database server — and the
  shipped sample runs the same in one console run
