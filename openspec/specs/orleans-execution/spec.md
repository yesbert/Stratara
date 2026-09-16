# orleans-execution Specification

## Purpose
Run commands, projections, sagas, timers and singleton work as virtual actors on a cluster, so that a
committed fact is never lost to a crash, one aggregate has one writer across the whole deployment,
and work that must happen once per cluster needs no lock.

## Requirements

### Requirement: One aggregate has one writer across the cluster

Where a host has registered the Orleans execution model for commands, a command that names an
aggregate SHALL run in that aggregate's activation, and two commands naming the same aggregate SHALL
NOT run concurrently anywhere in the cluster. Where an unstable cluster produces a second activation regardless, the
store's version constraint SHALL still refuse the second writer, so the guarantee degrades to the one
the bus workers give and never below it. A command that a handler sends for another aggregate SHALL
run in that other aggregate's activation, not in the sending handler's. Sends between aggregates
SHALL form a directed acyclic graph: a send that names an aggregate whose turn the sending chain is
already inside SHALL be refused at once, with a message naming the sending and the receiving
aggregate, rather than waiting for a timeout.

A heavy command SHALL be the exception: it SHALL run in the bounded heavy-work pool, outside its
aggregate's activation, so that a long unit does not hold the aggregate's other commands back. It MAY
therefore run while another command naming the same aggregate runs; where both append, the store's
version constraint SHALL refuse the later writer, and the refused command SHALL be resumed like any
failing command — the guarantee the bus path gives heavy work.

#### Scenario: Two commands name one aggregate from two hosts

- **WHEN** two hosts dispatch commands naming the same aggregate at the same time
- **THEN** one runs after the other, in one activation, and no concurrency conflict is raised

#### Scenario: The cluster is unstable and activates an aggregate twice

- **WHEN** a directory lapse lets two activations of one aggregate exist
- **THEN** at most one of their appends succeeds and the other observes a concurrency conflict, as it
  would on the bus path

#### Scenario: A handler sends a command for another aggregate

- **WHEN** a handler running for one aggregate dispatches a command naming a second aggregate while a
  command for the second aggregate is running
- **THEN** the dispatched command runs after the running one, in the second aggregate's activation

#### Scenario: A handler sends back to an aggregate in its own chain

- **WHEN** a handler running for aggregate A sends a command to aggregate B, and B's handler sends a
  command naming A while A's turn is still waiting on B
- **THEN** the send to A is refused at once with a message naming A and B, B's command fails with that
  refusal, A's command fails with B's failure, and neither activation waits for a timeout

#### Scenario: A heavy command and a command name one aggregate

- **WHEN** a heavy command naming an aggregate runs and a command naming the same aggregate is
  dispatched
- **THEN** the command runs without waiting for the heavy command, and if both append, one append
  succeeds and the other command observes a concurrency conflict and is resumed

### Requirement: An accepted command is recorded before the call returns and resumed after a crash

Where a host has registered the Orleans execution model's command dispatcher, dispatching a command
SHALL record it durably before the dispatch returns and hand it to its activation afterwards. A
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
SHALL name it as a setting the host sizes. Recorded commands SHALL be resumed by the
execution model's drain wherever that drain runs with an intent store registered, whichever host
dispatched them, SHALL never be published to a message bus, and a drain that finds recorded commands
without an intent store SHALL report it.

Every command on this path SHALL pass through the same mediator pipeline — validation, authorization,
tenant isolation, audit — as a command on the bus path, and an enqueue-time authorization the host
registered SHALL apply whatever order it and the execution model were registered in.

#### Scenario: The host dies after acceptance

- **WHEN** the dispatch has returned and the host is killed before the handler ran
- **THEN** the command runs after the host or another silo resumes it — verified with twenty kills
  on the PostgreSQL store

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
- **THEN** it is not handed over again while it runs, and it runs once

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

### Requirement: Projections and sagas read the store in commit order and never miss a committed fact

Where a host has registered the Orleans execution model for projections or sagas, each SHALL read
the event store from a checkpoint, in an order in which no entry at or below a checkpoint can still
commit later, and apply what it reads under the session recorded with each entry. A commit SHALL
wake the readers of the partitions it touched; a wake-up that is lost costs latency and never a
fact, because a poll reads the store regardless. A committed fact SHALL reach every projection and
saga whatever dies after the commit. Two rebuilds of one projection SHALL NOT interleave: the
projection's readers SHALL resume only when every rebuild that paused them has finished, and a
rebuild requested while a full replay is active SHALL be refused with a message that says so.

#### Scenario: The host dies between the commit and the wake-up

- **WHEN** a host is killed after committing events and before any wake-up or publication
- **THEN** every projection and saga applies those events after the host or another silo reads the
  store — verified with twenty kills on the PostgreSQL store, none lost

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

### Requirement: A failing entry stops its partition, is retried, and is visible

Where an entry cannot be applied — a missing prerequisite past its retry policy, or a genuine
failure — the reader SHALL stop at that entry, SHALL NOT advance its checkpoint past it, SHALL retry
it on the next wake-up or poll, and SHALL log the entry and count the stall, so that a partition that
stops advancing is seen and not inferred from a checkpoint that stands still. A read that fails
before any entry is applied — the store unreachable, a checkpoint the reader refuses — SHALL count as
a stall and be logged whichever wake-up or poll started it. A batch whose application is cut short by
the reader's own shutdown SHALL still record the checkpoint for the entries it applied.

#### Scenario: A projection throws on one entry

- **WHEN** a projection throws while applying an entry
- **THEN** the checkpoint stays before that entry, the failure is logged with the entry's identity,
  the stall is counted, and the next wake-up tries the entry again

#### Scenario: A fact from another partition has not been applied yet

- **WHEN** a projection reports that a prerequisite is missing
- **THEN** the entry is retried under the preceding-fact policy without advancing the checkpoint,
  and applies once the prerequisite has

#### Scenario: The store cannot be read

- **WHEN** a reader's read of the store fails on a poll
- **THEN** the failure is logged with the consumer and partition, the stall is counted, and the next
  wake-up or poll reads again

### Requirement: Work that must happen once happens once per cluster

Singleton work SHALL run in one place in the cluster at its period, without a lock, only on a silo
that registered it, and SHALL resume elsewhere when the silo running it is lost. Owner-checked durable
timers SHALL fire once per cluster on or after their due time, SHALL fire for an owner that exists and
never for one that was removed, and SHALL survive a restart of the silo that registered them. A timer
SHALL NOT fire a further period late because the clocks of the silos differ slightly. A process
timeout registered in a step SHALL survive a kill at any point of that step, and a timeout whose
handling cancels or registers the process's timers SHALL complete.

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

### Requirement: Heavy work is bounded across the cluster by permits that expire with their holder

Heavy commands SHALL run in a bounded worker pool per silo and under a cluster-wide bound of
permits. A permit SHALL expire when the silo holding it is no longer a member of the cluster or its
lease has lapsed, so that crashed workers do not shrink the bound. Interactive commands SHALL NOT
queue behind heavy work.

#### Scenario: A worker silo dies holding permits

- **WHEN** a silo holding heavy-work permits is killed
- **THEN** its permits are released once the cluster has declared it dead or the lease has lapsed,
  and the bound is whole again

#### Scenario: Interactive commands during a heavy burst

- **WHEN** interactive commands are dispatched while the heavy pool is saturated
- **THEN** their latency stays within its no-burst range — verified with a saturated heavy pool on
  the PostgreSQL store

### Requirement: A host can reset what the execution model keeps outside the event stream

A host SHALL be able to clear, deterministically, everything the execution model keeps beside the
event stream for its own deployment — the reminders of its service, the membership of its cluster, the
grain directory's entries and the checkpoints of the store-reading projections and sagas the host
registers — so that the deployment can be brought back to "nothing scheduled, nothing remembered". A
reset SHALL NOT remove a reminder, membership row or checkpoint that belongs to another deployment or
to a consumer the host does not register, and SHALL report how many of each it removed. The event
stream SHALL NOT be touched by a reset. A host SHALL be able to name the schema its runtime tables
live in, and a reset that finds a runtime table absent SHALL fail naming it rather than report that it
removed nothing. The documentation SHALL say that a reset that fails part-way leaves what it had not
yet removed in place, and SHALL show the reset resolved in a way that works wherever scope validation
is on.

#### Scenario: A reset is run

- **WHEN** a host runs the reset
- **THEN** no reminder of its service, membership row of its cluster, directory entry or checkpoint of
  a projection or saga it registers remains, the event stream is intact, and a restart fires nothing
  and rebuilds those checkpoints from the stream

#### Scenario: The read store holds another consumer's checkpoints

- **WHEN** a host runs the reset against a read store that also holds the checkpoints of a projection
  the host does not register
- **THEN** those checkpoints remain with the positions they had, and the report does not count them

#### Scenario: The runtime tables live in a schema

- **WHEN** a host whose reminder and membership tables live in a schema other than the connection's
  default names that schema and runs the reset
- **THEN** its reminders and membership rows are removed and counted

#### Scenario: A runtime table is absent

- **WHEN** a host runs the reset against a database in which a reminder or membership table does not
  exist under the named schema
- **THEN** the reset fails with a message naming the table, and reports no count

### Requirement: A read store's checkpoints belong to the consumers that read into it

A checkpoint SHALL be identified by the consumer that reads the store and the partition it reads, not
by the deployment that runs the consumer. Two deployments SHALL be able to keep their checkpoints in
one read store only when no consumer name is registered by both; store-reading sagas count as one
consumer across all deployments, so at most one deployment sharing a read store SHALL run them. The
documentation SHALL state this where a read store is configured for the execution model.

#### Scenario: Two deployments share a read store with distinct projections

- **WHEN** two deployments register store-reading projections with different names against one read
  store and both run
- **THEN** each deployment's projections advance their own checkpoints, and neither deployment's
  reading or reset changes the other's

#### Scenario: Two deployments share a read store and both run store-reading sagas

- **WHEN** two deployments that share a read store both run store-reading sagas
- **THEN** the documentation names this as unsupported, because both write the same checkpoints

### Requirement: A restart and a hard death behave as stated, and the documentation says so

A silo restarted on the endpoint it had SHALL rejoin without waiting for its earlier membership
entry to expire, and SHALL fire a timer that came due while it was down once it is back. A silo joining on a **different** endpoint while a
killed silo's membership entry is still active SHALL NOT be assumed to join: it waits for the
runtime's join window and then fails, and the documentation SHALL state the three answers — a second
active silo that votes the dead one out, a restart on the dead silo's endpoint, or a cleanup of the
membership table — rather than imply the runtime recovers on its own.

#### Scenario: A silo restarts on its own endpoint

- **WHEN** a single silo is killed and restarted on the same endpoint
- **THEN** it rejoins without waiting for a membership timeout and a timer registered before the kill
  fires — verified on Orleans 10.3.1 with the PostgreSQL membership table

#### Scenario: A replacement silo starts on another endpoint after a hard death

- **WHEN** the only silo is killed and a replacement starts on another endpoint
- **THEN** the replacement does not join within the runtime's join window, and the operations
  documentation names the three ways out — verified on Orleans 10.3.1 with the PostgreSQL
  membership table, under the default and the shortened liveness settings alike

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
