# orleans-execution

## MODIFIED Requirements

### Requirement: Work that must happen once happens once per cluster

Singleton work SHALL run in one place in the cluster at its period, without a lock, only on a silo
that registered it, and SHALL resume elsewhere when the silo running it is lost. Where the cluster
declares a silo dead that is still running, the work MAY run on a second silo from that declaration
until the declared silo learns of it and stops, so a consumer's singleton work SHALL tolerate a run
overlapping with one on another host, as the framework's own outbox drain does; the documentation
SHALL state that window, SHALL name how long a failover takes in terms of the cluster's membership
settings and the work's keep-alive period, and SHALL say that the window is bounded by those settings
only while the declared silo can still read the cluster's membership — one that cannot never learns of
its declaration. A run that fails SHALL be logged with an event of the framework's own, whatever it
failed with, except where the silo it runs on is stopping. Two works SHALL NOT be registered under one
name: the name is what a work's single run is keyed by, so the second would never run. Owner-checked durable
timers SHALL fire once per cluster on or after their due time, SHALL fire for an owner that exists and
never for one that was removed, and SHALL survive a restart of the silo that registered them. A timer
SHALL NOT fire a further period late because the clocks of the silos differ slightly. A process
timeout registered in a step SHALL survive a kill at any point of that step, and a timeout whose
handling cancels or registers the process's timers SHALL complete.

A timer's handler SHALL receive a cancellation token that is requested when the silo running it
stops and the handler has not completed within the runtime's deactivation budget; a timer whose
handler stops on that token SHALL stay registered and fire on the next silo, which is the at-least-once
delivery the timers promise, and the stop SHALL be logged with the owner and the purpose. A timer
registered for an owner and purpose while a tick for that owner and purpose is being handled SHALL be
kept and SHALL fire, whether or not its due time is the one being handled. An owner id longer than
the timer store holds SHALL be refused on registration, cancellation and listing with a message
naming the limit and the length given, as a purpose is, so that the refusal is seen at the
registration and not at the first tick.

A run of a singleton work that fails SHALL be logged with an event of the framework's own, naming the
work and carrying the failure, and SHALL NOT stop the work: the next run goes ahead at its period. A
singleton work registered with the name it publishes under SHALL NOT be constructed before the silo
is active, so that a work whose construction needs the running host is not constructed while the
silo starts; a work whose name differs from the one it was registered with SHALL fail the silo's
start with a message naming both.

#### Scenario: Two silos run the same singleton work

- **WHEN** two silos register the same singleton work
- **THEN** it runs once per period in the cluster, not once per silo

#### Scenario: A timer's owner was removed before it was due

- **WHEN** a timer is due and its owner no longer exists
- **THEN** the timer unregisters itself and no handler runs

#### Scenario: The silo is killed with timers open

- **WHEN** a silo is killed with registered timers and restarted
- **THEN** every kept owner's timer fires exactly once and no removed owner's timer fires — verified
  with ten kills on the PostgreSQL reminder table

#### Scenario: Silos register different singleton work

- **WHEN** one silo registers a singleton work and another silo of the cluster does not
- **THEN** the work runs on the silo that registered it, and the other silo reports no failure for it

#### Scenario: The silo running singleton work is lost

- **WHEN** the silo running a singleton work is killed while another silo that registered it stays
- **THEN** the work runs on the remaining silo at its period

#### Scenario: The silo running singleton work is declared dead while it still runs

- **WHEN** the cluster declares dead a silo that is still running a singleton work, because its probes
  went unanswered
- **THEN** the work is brought up on another silo that registered it, both MAY run until the declared
  silo has stopped itself, and the operations documentation names that window and the settings that
  bound the failover — the membership probe settings, the reminder refresh and the keep-alive period

#### Scenario: A timeout changes the process's timers

- **WHEN** a process handles a timeout by cancelling or registering timers, or a fact for the process
  arrives while its timeout is being handled
- **THEN** the handling completes and the timeout does not run again

#### Scenario: The host dies inside a step that schedules a timeout

- **WHEN** a silo is killed at any point of a step that schedules a timeout, and the fact is applied
  again after the restart
- **THEN** the timeout fires once, and a timeout for a step whose events were never recorded reaches the
  process with the state as recorded

#### Scenario: A tick arrives shortly before the due time

- **WHEN** a timer's tick runs on a silo whose clock is slightly behind the registering host's
- **THEN** the timer fires on that tick and not a retry period later

#### Scenario: A timer's handler runs longer than the retry period

- **WHEN** a timer's handler is still running when the timer's next tick arrives
- **THEN** the tick does not start the handler again, and the handler runs once

#### Scenario: A silo stops while a timer's handler runs

- **WHEN** a silo is stopped while a timer's handler is waiting on its cancellation token, and the
  timer's owner still exists
- **THEN** the handler observes the cancellation within the deactivation budget, the stop is logged
  with the owner and the purpose, the timer is still registered, and it fires on the next silo that
  serves the owner — verified on the PostgreSQL reminder table

#### Scenario: A timer is re-registered from its own handler with the same due time

- **WHEN** a timer's handler registers a timer for the same owner and purpose with the same due time
  and returns
- **THEN** the timer is still registered when the handler has returned, and it fires again within a
  retry period

#### Scenario: A timer is re-registered from its own handler with a later due time

- **WHEN** a timer's handler registers a timer for the same owner and purpose with a later due time
  and returns
- **THEN** only the later timer is registered when the handler has returned, and it fires at its due
  time

#### Scenario: An owner id is longer than the timer store holds

- **WHEN** a timer is registered, cancelled or listed for an owner id longer than the timer store
  holds
- **THEN** the call is refused with a message naming the limit and the length given, and nothing is
  registered

#### Scenario: A singleton work's run fails

- **WHEN** a run of a singleton work throws
- **THEN** an event of the framework's names the work and carries the failure, and the work runs again
  at its next period

#### Scenario: A work is registered with its name

- **WHEN** a host registers a singleton work with the name it publishes under, and the work's
  constructor records when it runs
- **THEN** the work is not constructed before the silo is active, and it runs once per period on the
  silo as any other

#### Scenario: A work's name differs from the registered one

- **WHEN** a host registers a singleton work under a name that is not the work's `Name`
- **THEN** the silo fails at start with a message naming both

### Requirement: The execution model can be adopted per role beside the bus workers

Each role — commands, projections, sagas, outbox drain, timers, heavy work — SHALL be adopted with
one registration after the role's existing composite, and a host SHALL be able to run both models
at once during a rollout, because both apply idempotently. A host that asks the projection or saga
registration to keep publishing bundles to the bus SHALL keep the bus dispatcher it registered
whatever the shape of that registration, and a host that asks for it without having registered a
bus dispatcher SHALL fail at registration with a message naming what is missing rather than run
without publishing. Each of the model's registrations SHALL
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

#### Scenario: Two singleton works ask for one name

- **WHEN** a host registers two works under the same name
- **THEN** the registration fails naming both works and the name

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

- **WHEN** a silo registers the command role and no projection or saga role, and a handler on it
  commits facts
- **THEN** the bundle is published to the bus as before, and the documentation states that such a
  silo keeps the broker until it registers a store-reading role

#### Scenario: A consumer tests on the execution model

- **WHEN** a consumer follows the testing documentation for the execution model
- **THEN** its handlers, projections, sagas and timers run through the registrations it uses in
  production, in the test's process, without a cluster, a broker or a database server — and the
  shipped sample runs the same in one console run
