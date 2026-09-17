## MODIFIED Requirements

### Requirement: An accepted command is recorded before the call returns and resumed after a crash

Where a host has registered the Orleans execution model's command dispatcher, dispatching a command
SHALL record it durably before the dispatch returns and hand it to its activation afterwards. The
record SHALL be committed on its own, not with anything the caller writes in its own unit of work,
so that a caller whose own save fails after the dispatch still has a recorded command that runs, and
the documentation SHALL say so where it introduces the record. A
host that dies between acceptance and completion SHALL resume the command after a configurable
grace. A command whose completion the host had not recorded before it died SHALL run again. A command
whose handler keeps failing SHALL be resumed a bounded number of times and then kept for an operator,
as a bus message a handler cannot take is kept, and SHALL NOT hold back the resumption of other
commands; an operator SHALL be able to return a kept command, which is then resumed with its attempts
starting over. Every attempt that fails SHALL be logged with the command's identity, the aggregate it
names and its type, and so SHALL a hand-over that fails, so that a failing handler is seen before the
command is kept; the resumption that follows SHALL log the attempt number. A command whose handler is still running SHALL NOT be handed over
again, however long it runs and whether or not the handler yields, and a heavy command waiting for a
worker or a permit SHALL count as running. A resumed command SHALL keep the order of the aggregate
it names whatever protection its payload carries. Heavy commands SHALL be exempt from both order
promises, and a heavy command SHALL NOT hold back a command or a resumption that follows it. The
commands waiting in an aggregate's order SHALL run to the end however long the order takes; the
runtime's response timeout bounds a caller's wait for one forwarded command, and the documentation
SHALL name it as a setting the host sizes. A forwarded command whose handler outlasts that timeout
SHALL be reported to its caller as timed out while its handler runs to the end and its append commits;
the documentation SHALL state this and SHALL say that a caller does not retry on a timeout, because
the retry would run the command a second time. Recorded commands SHALL be resumed by the
execution model's drain wherever that drain runs with an intent store registered, whichever host
dispatched them, SHALL never be published to a message bus, and a drain that finds recorded commands
without an intent store SHALL report it. A resumption the drain holds back because a full replay is
active SHALL be logged when the holding back begins and when it ends, so that a recorded command
that waits for the length of a replay is seen waiting rather than lost.

Where the host has registered a bus-envelope signer, the record SHALL carry a signature over the same
claims a command on the bus is signed over — its type, its session context, its heavy flag and a
digest of its body — and a command resumed from its record SHALL be verified under the host's
integrity mode before it is handed over, so that the session a resumed command runs under is the one
it was dispatched under. Under strict mode a record that carries no signature or one that does not
verify SHALL be kept for an operator at once, without an attempt, with the reason recorded with it;
under permissive mode it SHALL be resumed and the failure recorded; the two failures SHALL be
distinguishable by the identity of the record, as they are on the bus. A command handed over without
passing through storage is not verified. A record written before the host signed carries no
signature, and the documentation SHALL name the rollout through permissive mode, as it does for the
bus.

The resumption of a backlog SHALL NOT be bounded by the drain's period: while a pass finds as many
due commands as it asked for, the next pass SHALL follow at once, until a pass finds fewer or the run
has lasted its period, and the next run SHALL continue; and the store round trips of one pass SHALL
NOT grow with the number of commands it claims.

Every command on this path SHALL pass through the same mediator pipeline — validation, authorization,
tenant isolation, audit — as a command on the bus path, and an enqueue-time authorization the host
registered SHALL apply whatever order it and the execution model were registered in. A resumed command
SHALL run under the session recorded with it, on a silo and without the request that dispatched it, so
an authorization the host registers SHALL be answered from that session; the documentation SHALL say
that a provider which answers from the current web request refuses every resumed command, which is
then kept after its attempts, and SHALL name a session-driven provider as the shape this path needs.

A handler running on this path SHALL receive a cancellation token that is requested when the silo
running it stops and the handler has not completed within the runtime's deactivation budget, so
that a handler can stop cleanly instead of running on in a process that is going away. A recorded
command whose handler stops on that token SHALL be resumed after the grace on a silo that is still
running, as after a crash, and the stop SHALL NOT count as a failed attempt. A forwarded command
whose handler stops on that token SHALL fail back to its caller with a message saying that the silo
stopped and the command may be dispatched again. A handler that does not observe the token SHALL run
to its end, and the silo SHALL wait for it as long as the runtime's deactivation allows. Every handler
stopped this way SHALL be logged with the command's identity. The documentation SHALL name the
deactivation budget as a setting the host sizes and SHALL say what a handler is expected to do with
the token.

#### Scenario: The host dies after acceptance

- **WHEN** the dispatch has returned and the host is killed before the handler ran
- **THEN** the command runs after the host or another silo resumes it — verified with five kills per
  path, recorded and heavy, on the PostgreSQL store

#### Scenario: The host dies after the handler completed

- **WHEN** the handler completed and the host is killed before the completion was recorded
- **THEN** the command runs a second time, which the handler tolerates as it does under at-least-once
  delivery on the bus

#### Scenario: A handler keeps failing

- **WHEN** a resumed command's handler throws on every attempt
- **THEN** each attempt is logged with the command's identity and each resumption with its attempt
  number, it is resumed up to the configured bound, then kept with the attempt count and the last
  failure, and the commands after it are still resumed

#### Scenario: An operator returns a kept command

- **WHEN** an operator returns a kept command through the documented procedure
- **THEN** it is resumed with its attempt count starting over

#### Scenario: A handler runs longer than the grace

- **WHEN** a recorded command's handler is still running after the grace has passed
- **THEN** it is not handed over again while it runs, and it runs once — verified on the aggregate path
  for a handler that awaits and for one that computes without yielding, and on the heavy path for a
  handler that awaits

#### Scenario: A heavy handler computes past the grace without yielding

- **WHEN** a heavy command's handler runs longer than the grace without awaiting anything
- **THEN** it is not handed over again while it runs, it runs once, and its permit is still held when
  it ends

#### Scenario: A heavy burst queues commands longer than the grace

- **WHEN** more heavy commands are dispatched than the pool runs at once, so that a command waits for a
  worker longer than the grace
- **THEN** every command runs exactly once

#### Scenario: An aggregate's order takes longer than the response timeout

- **WHEN** commands accepted into one aggregate's order take longer to run, together, than the
  runtime's response timeout
- **THEN** every accepted command runs, in order, and none is failed back to its caller as not having
  run

#### Scenario: A forwarded command's handler outlasts the response timeout

- **WHEN** a command that names an aggregate is dispatched through the mediator and its handler runs
  longer than the runtime's response timeout
- **THEN** the caller observes a timeout, the handler runs once to the end, its append is committed,
  and the operations documentation says that a caller does not retry on a timeout — verified with a
  shortened response timeout on the PostgreSQL store

#### Scenario: A caller's own unit of work fails after the dispatch

- **WHEN** a caller dispatches a command through the recording dispatcher inside a unit of work of its
  own and that unit of work is then abandoned without a save
- **THEN** the command is recorded and runs, because the record was committed on its own — verified on
  the PostgreSQL store

#### Scenario: A resumed command is authorized from its recorded session

- **WHEN** a host registers an authorization provider that answers from the session context, dispatches
  a role-guarded command that the dispatching session is entitled to, and is killed before the handler
  ran
- **THEN** the resumed command is authorized on the silo as it was at dispatch and its handler runs;
  and where the host's provider answers from the current web request instead, every resumed command is
  refused and kept after its attempts, as the documentation says — verified on the PostgreSQL store

#### Scenario: A resumed command's payload is encrypted

- **WHEN** a command whose payload is encrypted is resumed after a crash
- **THEN** it runs in the activation of the aggregate it names, after the commands before it

#### Scenario: A command the host's pipeline rejects is dispatched

- **WHEN** a command that the host's validation or authorization rejects is dispatched on this path
- **THEN** it is rejected as it would be on the bus path, and its handler does not run

#### Scenario: Enqueue-time authorization is registered after the execution model

- **WHEN** a host registers enqueue-time authorization before or after the execution model's dispatcher
- **THEN** an unauthorised dispatch is refused and an authorised one reaches its activation, in both
  orders

#### Scenario: Two commands to one aggregate from one scope

- **WHEN** one scope dispatches two commands naming the same aggregate, one after the other
- **THEN** they run in the order they were dispatched; across scopes no order is promised, and a
  heavy command keeps no order with the commands around it

#### Scenario: The drain runs on a silo that did not dispatch the commands

- **WHEN** commands are recorded by an API host and the drain runs on a silo where the execution
  model's dispatcher is not registered, and a recorded command is due or kept
- **THEN** the due command is resumed within its attempt bound, the kept command stays kept, and
  neither is published to a bus

#### Scenario: Two heavy commands to one aggregate are due

- **WHEN** two heavy commands naming the same aggregate are due in one resumption, and further
  commands are due after them
- **THEN** every due command is handed over without waiting for either heavy command to finish

#### Scenario: A resumption is held back by a replay

- **WHEN** a recorded command is due while a full replay is active, and the replay then ends
- **THEN** the drain logs once that it is holding resumptions back and once that it has resumed them,
  not once per period in between, and the command is resumed after the replay

#### Scenario: A silo stops while a recorded command's handler runs

- **WHEN** a silo is stopped while a recorded command's handler is waiting on its cancellation token,
  and another silo of the cluster stays
- **THEN** the handler observes the cancellation within the deactivation budget, the stop is logged
  with the command's identity, the command is resumed on the remaining silo after the grace with no
  attempt counted, and it completes there — verified on the PostgreSQL store

#### Scenario: A silo stops while a forwarded command's handler runs

- **WHEN** a silo is stopped while a forwarded command's handler is waiting on its cancellation token
- **THEN** the handler observes the cancellation, and the caller's dispatch fails with a message
  saying the silo stopped and the command may be dispatched again

#### Scenario: A handler ignores the cancellation

- **WHEN** a silo is stopped while a handler that does not observe its token is running
- **THEN** the handler runs to its end, and the silo waits for it up to the runtime's deactivation
  budget before it stops

#### Scenario: A recorded command's session is altered in storage

- **WHEN** a host with a signer in strict mode has recorded a command, its stored session context is
  altered before the host dies, and the drain finds the record due
- **THEN** the command is kept at once with the reason that its signature did not verify, its handler
  does not run, and the record that says so is distinct from the one for an unsigned command —
  verified on the PostgreSQL store

#### Scenario: A record written before the host signed is resumed

- **WHEN** a record without a signature is due on a host with a signer
- **THEN** in permissive mode it is resumed and a record states that it was unsigned; in strict mode it
  is kept at once with that reason; with the mode off it is resumed as it always was

#### Scenario: A backlog larger than one batch is due

- **WHEN** more recorded commands than the drain's batch size are due when the drain runs — as after
  an outage of the hosts that dispatch
- **THEN** every one of them is handed over within one period of the drain, not one batch per period
  — verified on the PostgreSQL store with a backlog of several batches

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

### Requirement: Work that must happen once happens once per cluster

Singleton work SHALL run in one place in the cluster at its period, without a lock, only on a silo
that registered it, and SHALL resume elsewhere when the silo running it is lost. Where the cluster
declares a silo dead that is still running, the work MAY run on a second silo from that declaration
until the declared silo learns of it and stops, so a consumer's singleton work SHALL tolerate a run
overlapping with one on another host, as the framework's own outbox drain does; the documentation
SHALL state that window and SHALL name how long a failover takes in terms of the cluster's membership
settings and the work's keep-alive period. Owner-checked durable
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
