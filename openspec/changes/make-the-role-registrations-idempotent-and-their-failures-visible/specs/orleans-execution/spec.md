## MODIFIED Requirements

### Requirement: Work that must happen once happens once per cluster

Singleton work SHALL run in one place in the cluster at its period, without a lock, only on a silo
that registered it, and SHALL resume elsewhere when the silo running it is lost. Owner-checked durable
timers SHALL fire once per cluster on or after their due time, SHALL fire for an owner that exists and
never for one that was removed, and SHALL survive a restart of the silo that registered them. A timer
SHALL NOT fire a further period late because the clocks of the silos differ slightly. A process
timeout registered in a step SHALL survive a kill at any point of that step, and a timeout whose
handling cancels or registers the process's timers SHALL complete.

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
at once during a rollout, because both apply idempotently. Each of the model's registrations SHALL
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
