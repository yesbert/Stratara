## MODIFIED Requirements

### Requirement: The execution model can be adopted per role beside the bus workers

Each role — commands, projections, sagas, outbox drain, timers, heavy work — SHALL be adopted with
one registration after the role's existing composite, and a host SHALL be able to run both models
at once during a rollout, because both apply idempotently. A host that asks the projection, saga or store
wake-up registration to keep publishing bundles to the bus SHALL keep the bus dispatcher it registered
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
open no broker connection. Any host joined to the cluster, as a silo or as a client, SHALL be able to
have its bundle dispatcher replaced without hosting a reader by registering the store wake-ups. After a
commit, such a host SHALL wake the store readers of every projection and saga role the cluster hosts for
each partition the commit touched, so that they apply the commit's facts without waiting for their poll. It
SHALL store nothing in the outbox and SHALL publish nothing to the bus unless it asked to keep the bus, and a
host so composed that commits facts in process SHALL open no broker connection. On a silo with a
store-reading role, the store wake-ups SHALL add the roles the silo does not host. A role that no silo of
the cluster hosts SHALL NOT fail the commit and SHALL NOT be called on every commit. A wake-up that cannot be
sent SHALL be lost without failing the commit, and the reader's poll SHALL apply the fact. A host that
commits facts and has neither a store-reading role nor the store wake-ups SHALL keep publishing bundles to
the bus, because nothing on it wakes a reader. The documentation SHALL say so, SHALL name the store wake-ups
as the registration that removes the broker from such a host, and SHALL NOT describe a host as committing
nothing because it only dispatches through the execution model.
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

- **WHEN** a host asks the projection, saga or store wake-up registration to keep publishing to the bus
  while no bus bundle dispatcher is registered
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
  timer, dispatcher, store wake-up, heavy-work or singleton-work registration
- **THEN** the composition is the one a single call leaves: each projection is woken once per bundle,
  the seeding and the reset list each consumer once, and each work runs once per period

#### Scenario: A silo registers the directory itself and hosts a role

- **WHEN** a silo registers a grain directory under the model's name directly, not through the call
  that publishes, and registers a role or a singleton work
- **THEN** the silo fails at start with a message naming the roles and works it registered and the
  call that publishes them

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

- **WHEN** a silo registers the command role and neither a projection or saga role nor the store
  wake-ups, and a handler on it commits facts
- **THEN** the bundle is published to the bus as before, and the documentation states that such a
  silo keeps the broker until it registers a store-reading role or the store wake-ups

#### Scenario: A client host commits facts in process

- **WHEN** a host joined to the cluster as an Orleans client registers the store wake-ups and no broker,
  and a command handler it runs in process through the mediator commits a fact
- **THEN** a projection on a silo applies the fact well before its poll interval has passed, nothing is
  stored in the outbox, no publish failure is logged, and no broker connection is attempted — verified on
  the PostgreSQL store with a poll interval far longer than the test's wait

#### Scenario: A command silo without a store-reading role registers the store wake-ups

- **WHEN** a silo registers the command role and the store wake-ups, the projection and saga roles run on
  another silo, and a handler on the command silo commits facts
- **THEN** the projection and the saga on the other silo are woken and apply the facts before their poll
  interval has passed, and no bundle reaches the bus or the outbox

#### Scenario: A silo with one store-reading role registers the store wake-ups

- **WHEN** a silo registers the saga role and the store wake-ups, the projection role runs on another
  silo, and a handler on the first silo commits facts
- **THEN** the sagas and the projections are both woken, each once per partition the commit touched

#### Scenario: The store wake-ups and a store-reading role are registered in either order

- **WHEN** a host registers the store wake-ups before a store-reading role, or after it
- **THEN** the composition is the same either way: one bundle dispatcher that wakes every store-reading
  role of the cluster, and a bus dispatcher kept only where a registration asked for it

#### Scenario: The cluster hosts no saga role

- **WHEN** a host with the store wake-ups commits facts repeatedly in a cluster in which no silo
  registered the saga role
- **THEN** every commit succeeds, the projections are woken, the saga role is not called on every commit,
  and once a silo registering the saga role joins, its sagas are woken again within the period the
  wake-up sets an absent role aside for

#### Scenario: A client host keeps the bus beside the wake-ups

- **WHEN** a host with a bus bundle dispatcher registers the store wake-ups asking to keep the bus, and
  commits facts
- **THEN** each bundle still reaches the bus dispatcher, and the store readers are woken

#### Scenario: A consumer tests on the execution model

- **WHEN** a consumer follows the testing documentation for the execution model
- **THEN** its handlers, projections, sagas and timers run through the registrations it uses in
  production, in the test's process, without a cluster, a broker or a database server — and the
  shipped sample runs the same in one console run
