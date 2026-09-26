## MODIFIED Requirements

### Requirement: An accepted command is recorded before the call returns and resumed after a crash

Where a host has registered the Orleans execution model's command dispatcher, dispatching a command
SHALL record it durably before the dispatch returns and hand it to its activation afterwards. The
record SHALL be committed on its own, not with anything the caller writes in its own unit of work,
so that a caller whose own save fails after the dispatch still has a recorded command that runs, and
the documentation SHALL say so where it introduces the record. A
host that dies between acceptance and completion SHALL resume the command after a configurable
grace. A command whose completion the host had not recorded before it died SHALL run again. A command whose handler keeps failing SHALL run no more often than a bus message a handler cannot
take is delivered — the configured delivery bound counts the first hand-over as its first attempt —
and then be kept for an operator, as that message is kept, and SHALL NOT hold back the resumption of
other commands. A handler that fails on a concurrency conflict SHALL be bounded by the conflict bound
the host configures for the bus, not by the delivery bound, so that a command on a contended aggregate
is kept no sooner than the same command on the bus; an operator SHALL be able to return a kept command, which is then resumed with its attempts
starting over. Every attempt that fails SHALL be logged with the command's identity, the aggregate it
names and its type, and so SHALL a hand-over that fails, so that a failing handler is seen before the
command is kept; the resumption that follows SHALL log the attempt number. A command whose handler is still running SHALL NOT be handed over
again, however long it runs and whether or not the handler yields, and a heavy command waiting for a
worker or a permit SHALL count as running. A resumed command SHALL be run by one runner only: where two
resumptions hand the same command over — two drains during a failover, or a drain beside a host's own
resumption — or a hand-over arrives after the command completed, the hand-over that finds the command
already taken or gone SHALL be dropped without running the handler. A resumed command SHALL keep the order of the aggregate
it names whatever protection its payload carries, and that order SHALL be the order in which one scope
dispatched its commands, however long each took to be recorded. Whether a recorded command is due
SHALL be judged on the clock the host registers, so that a host or a test that registers its own clock
sees the grace it configured. Heavy commands SHALL be exempt from both order
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
passing through storage is not verified. Where a resumed command runs SHALL be taken from the claims
that are signed, not from anything beside them that is not: a record whose stored routing disagrees
with its signed envelope SHALL be treated as a record that does not verify, and the documentation
SHALL name what the signature covers and what it does not. A record written before the host signed carries no
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
stopped this way SHALL be logged with the command's identity.
A handler that fails with the event store's failure saying its events were committed but could not
be published — a cancellation after that commit included — has neither stopped nor failed in this
sense: its recorded command SHALL be completed, not resumed and not counted as a failed attempt, the
failure SHALL be logged with the command's identity and type at error level, and a forwarded
command's caller SHALL receive that failure. The documentation SHALL name the
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
  number, the handler runs as many times as the configured delivery bound — the first hand-over
  included — the command is then kept with the attempt count and the last failure, and the commands
  after it are still resumed

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

#### Scenario: A record's row disagrees with its signed envelope

- **WHEN** a recorded command's stored routing says something other than the signed envelope does
- **THEN** it is kept for an operator under strict mode with the reason recorded, and under permissive
  mode it is resumed as the signed envelope says and the failure logged

#### Scenario: Commands are queued behind a handler when the silo stops

- **WHEN** a silo stops while a forwarded command's handler runs and further commands for the same
  aggregate wait behind it
- **THEN** the callers of the waiting commands are told the activation ended before their command ran,
  without waiting for the running handler's activation to be collected, and none of those commands runs

#### Scenario: A record written before the host signed is resumed

- **WHEN** a record without a signature is due on a host with a signer
- **THEN** in permissive mode it is resumed and a record states that it was unsigned; in strict mode it
  is kept at once with that reason; with the mode off it is resumed as it always was

#### Scenario: A backlog larger than one batch is due

- **WHEN** more recorded commands than the drain's batch size are due when the drain runs — as after
  an outage of the hosts that dispatch
- **THEN** every one of them is handed over within one period of the drain, not one batch per period
  — verified on the PostgreSQL store with a backlog of several batches

#### Scenario: A resumed command meets a concurrency conflict on every attempt

- **WHEN** a recorded command's handler fails on a concurrency conflict on every attempt
- **THEN** it is resumed until the conflict bound is reached, not the delivery bound, and then kept
  with the conflict as its last failure

#### Scenario: Two drains resume the same command

- **WHEN** two resumptions claim and hand over the same due command at the same moment
- **THEN** its handler runs once, and the hand-over that arrived second is dropped — verified on the
  PostgreSQL store, for a command that names an aggregate and for one that names none

#### Scenario: A hand-over arrives after the command completed

- **WHEN** a resumed command completes and a second hand-over of it arrives afterwards
- **THEN** the second hand-over is dropped and the handler does not run again

#### Scenario: Two commands to one aggregate are recorded out of order

- **WHEN** one scope dispatches two commands naming the same aggregate, the first takes longer to be
  recorded than the second, and the host dies before either ran
- **THEN** the resumption runs them in the order they were dispatched

#### Scenario: A host registers its own clock

- **WHEN** a host registers its own clock and records a command, and that clock passes the grace
- **THEN** the command is due, whatever the wall clock says

#### Scenario: A recorded command committed but could not publish

- **WHEN** a recorded command's handler fails because its save committed its events but could not
  hand their bundle on
- **THEN** the command is completed, no failed attempt is recorded, it is not resumed, and the failure
  is logged with the command's identity

### Requirement: A failing entry stops its partition, is retried, and is visible

Where an entry cannot be applied — a missing prerequisite past its retry policy, or a genuine
failure — the reader SHALL stop at that entry, SHALL NOT advance its checkpoint past it, SHALL retry
it on the next wake-up or poll, and SHALL log the entry and count the stall, so that a partition that
stops advancing is seen and not inferred from a checkpoint that stands still. Where the failing entry
shares its position with entries before it that did apply — one transaction wrote them all — the
checkpoint SHALL stop below the whole group, and the group's applied entries SHALL be applied again
when the entry is retried, which a projection tolerates as it tolerates any second delivery. A read that fails
before any entry is applied — the store unreachable, a checkpoint the reader refuses — SHALL count as
a stall and be logged whichever wake-up or poll started it. A batch whose application is cut short by
the reader's own shutdown SHALL still record the checkpoint for the entries it applied. Each
store-reading saga SHALL read with a checkpoint of its own, so that an entry one saga cannot apply
stops that saga's reading of the partition only: every other saga SHALL apply the entry once and go on
past it, and SHALL NOT apply it again because another saga failed on it.

An entry whose handler fails with the event store's failure saying the handler's events were
committed but could not be published SHALL count as applied, exactly as an entry whose handler
succeeded does: the reader SHALL log it with the entry's identity at error level and go on past it
rather than stall on it, because retrying it would record the handler's facts a second time.

#### Scenario: A projection throws on one entry

- **WHEN** a projection throws while applying an entry
- **THEN** the checkpoint stays before that entry, the failure is logged with the entry's identity,
  the stall is counted, and the next wake-up tries the entry again

#### Scenario: A projection throws on the second entry of one transaction

- **WHEN** one transaction wrote two entries into a partition and a projection applies the first and
  throws on the second
- **THEN** the checkpoint stops below both, and the next read applies the first again before retrying
  the second

#### Scenario: A fact from another partition has not been applied yet

- **WHEN** a projection reports that a prerequisite is missing
- **THEN** the entry is retried under the preceding-fact policy without advancing the checkpoint,
  and applies once the prerequisite has

#### Scenario: The store cannot be read

- **WHEN** a reader's read of the store fails on a poll
- **THEN** the failure is logged with the consumer and partition, the stall is counted, and the next
  wake-up or poll reads again

#### Scenario: One saga throws on an entry another saga also handles

- **WHEN** two store-reading sagas react to the same fact and one of them throws on it on every attempt
- **THEN** the other applies the fact once and goes on to later facts of the partition, while the
  failing saga stays before the fact, logs it and counts the stall under its own consumer name

#### Scenario: A saga's step committed but could not publish

- **WHEN** a store-reading saga applies an entry and its save commits but cannot hand its bundle on
- **THEN** the entry counts as applied, the reader goes on past it and logs the failure with the
  entry's identity, and the step does not run again

### Requirement: Work that must happen once happens once per cluster

Singleton work SHALL run in one place in the cluster at its period, without a lock, only on a silo
that registered it, and SHALL resume elsewhere when the silo running it is lost. Where the cluster
declares a silo dead that is still running, the work MAY run on a second silo from that declaration
until the declared silo learns of it and stops, so a consumer's singleton work SHALL tolerate a run
overlapping with one on another host, as the framework's own outbox drain does; the documentation
SHALL state that window, SHALL name how long a failover takes in terms of the cluster's membership
settings and the work's keep-alive period, and SHALL say that the window is bounded by those settings
only while the declared silo can still read the cluster's membership — one that cannot never learns of
its declaration. A run that fails SHALL be logged whatever it failed with,
including a cancellation the work was not asked for, except where the silo it runs on is stopping. Two works SHALL NOT carry one name: the name is what a work's single run is keyed by, so the second would
never run, and a host that registers two SHALL fail rather than run one of them. Settings given with a
work's registration SHALL apply to that work alone; settings the host configures for singleton work as
a whole SHALL apply to every work whose registration does not set them; and a work registered twice
SHALL run with the settings of its later registration, once. Owner-checked durable
timers SHALL fire once per cluster on or after their due time, SHALL fire for an owner that exists and
never for one that was removed, and SHALL survive a restart of the silo that registered them. A timer
SHALL NOT fire a further period late because the clocks of the silos differ slightly. A process
timeout registered in a step SHALL survive a kill at any point of that step, and a timeout whose
handling cancels or registers the process's timers SHALL complete.

A timer's handler SHALL receive a cancellation token that is requested when the silo running it
stops and the handler has not completed within the runtime's deactivation budget; a timer whose
handler stops on that token SHALL stay registered and fire on the next silo, which is the at-least-once
delivery the timers promise, and the stop SHALL be logged with the owner and the purpose. A timer whose handler fails with the
event store's failure saying its events were committed but could not be published SHALL count as
fired — it SHALL NOT stay registered to fire again — and the failure SHALL be logged with the owner
and the purpose at error level. A timer
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

#### Scenario: Two singleton works carry one name

- **WHEN** a host registers two works under the same name, or two registered works return the same name
- **THEN** the host fails naming both works and the name, rather than running one of them

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

#### Scenario: Two works are registered with different keep-alive periods

- **WHEN** a host registers one singleton work with a keep-alive period of two minutes and another with
  five
- **THEN** the first is kept alive every two minutes and the second every five

#### Scenario: A work is registered twice with different settings

- **WHEN** a host registers the same singleton work twice, each time with a different keep-alive period
- **THEN** the work runs once, with the period of the later registration

#### Scenario: A timer's handler committed but could not publish

- **WHEN** a durable timer's handler fails because its save committed its events but could not hand
  their bundle on
- **THEN** the timer counts as fired and is not registered afterwards, and the failure is logged with
  the owner and the purpose

## ADDED Requirements

### Requirement: A framework failure keeps its type between silos

A failure the framework defines — a concurrency conflict, a save that committed but could not publish —
thrown on one silo SHALL reach a caller on another silo, or a client, with its type, its message and
its inner failures, whatever library those inner failures come from, so that the caller treats it as
it would in process. The registrations of the execution model SHALL arrange this without the host
having to, and a restriction the host placed on exception types SHALL keep applying. Properties of
such a failure beyond those need not cross; where they do not, they SHALL read as empty rather than
fail, and the message SHALL carry what they said.

#### Scenario: A handler on another silo reports a conflict

- **WHEN** a command forwarded to an aggregate on another silo fails with a concurrency conflict
- **THEN** the caller receives a concurrency conflict, and a transport that runs the forwarding
  handler treats it as a conflict rather than a failure — verified on the serializer round trip the
  runtime uses between silos

#### Scenario: A handler on another silo committed but could not publish

- **WHEN** a command forwarded to another silo fails because its save committed but could not
  publish
- **THEN** the caller receives that failure with its type, so nothing runs the command again
