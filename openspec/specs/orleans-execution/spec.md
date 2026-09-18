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
  would on the bus path — verified with two single-silo clusters that share one PostgreSQL store,
  which is the shape a lapse produces, each running a command for the same aggregate at once

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
could not pause, so that no partition is left waiting for a resume that never comes. A pause SHALL
belong to the one who paused and SHALL last only while that pauser keeps it: a pauser that stops —
its process dies part-way through a rebuild — SHALL leave its readers paused for no longer than a
bounded lease, after which they resume and the lapse is logged naming the reader. Resuming SHALL
release only the resuming pauser's own pause, however often the resume is repeated, so that a retried
resume never releases a pause another rebuild still holds. A rebuild or a replay SHALL leave its read
models holding every fact of the store however early a reader resumes during it — through a lapse, or
an activation that moved and no longer knows it was paused — so that a fact a reader applied before
the read model was emptied is applied again after it. Returning a
checkpoint to the beginning SHALL be accepted whatever reader last wrote it — the beginning means
the same under every reader and every partition count — so that a deployment whose reader or
partition count changed can rebuild or replay from inside the running cluster, and the documentation
SHALL name that as the way to recover from a refused checkpoint. A wake-up that cannot be sent SHALL
be logged with the consumers it was meant for, so that a wake-up path that is always lost is visible
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
- **THEN** the rebuild fails naming that partition, and no reader the rebuild reached stays paused

#### Scenario: A read model is rebuilt after the partition count changed

- **WHEN** a read model is rebuilt on a host whose checkpoints were written by another reader or under
  another partition count
- **THEN** the rebuild returns them to the beginning under the host's own reader and the read model is
  re-read, without stopping the deployment

#### Scenario: A wake-up cannot be sent

- **WHEN** a commit's wake-up cannot be delivered
- **THEN** it is logged naming the consumers it was meant for, and the consumers it could still reach are woken

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

#### Scenario: The process rebuilding a projection dies

- **WHEN** the process that paused a projection's readers for a rebuild dies before it resumes them
- **THEN** the readers resume by themselves once the pause's lease has passed, each lapse is logged
  naming the reader, and the projection's read model follows the store again without a silo restart

#### Scenario: A resume is repeated while a second rebuild holds the readers

- **WHEN** two rebuilds of one projection hold its readers paused, and the first rebuild's resume is
  delivered twice
- **THEN** the readers stay paused until the second rebuild resumes them

#### Scenario: A reader resumes while its read model is being emptied

- **WHEN** a reader of a projection being rebuilt resumes and applies facts before the read model is
  emptied
- **THEN** once the rebuild has finished and the readers have caught up, the read model holds every
  fact of the store, those facts included

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
the reader's own shutdown SHALL still record the checkpoint for the entries it applied.

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

### Requirement: Heavy work is bounded across the cluster by permits that expire with their holder

Heavy commands SHALL run in a bounded worker pool per silo and under a cluster-wide bound of
permits. A permit SHALL expire when the silo holding it is no longer a member of the cluster or its
lease has lapsed, so that crashed workers do not shrink the bound. Interactive commands SHALL NOT
queue behind heavy work.

The units a silo runs SHALL run beside each other up to the pool's bound, whether or not their
handlers yield: a unit whose handler computes without awaiting anything SHALL NOT keep another unit
of the silo from running, and SHALL NOT keep a further heavy command from being accepted and counted
as running while it waits for a worker.

The bound SHALL hold across the loss of the silo that keeps the permits, for every unit that
registers again within its lease. A keeper that takes over
after such a loss SHALL admit no new unit until every unit admitted by the lost keeper has had a
lease's time to register with it again, and a running unit whose permit was lost with its keeper
SHALL count against the bound again as soon as it has registered; a heavy command dispatched in that
window SHALL wait, as it waits when the bound is full, rather than start beside the running units.
A running unit that is refused when it registers again — one whose lease had already lapsed with the
lost keeper — SHALL keep running, because a running handler is not paused, SHALL keep asking on every
renewal until it holds a permit or ends, and SHALL be logged with an event of its own, so that an
operator can see a unit running outside the bound while it does. A permit that falls free while such
a unit is asking SHALL go to that unit and not to a command waiting to start, so that the bound is
exceeded only until a running unit ends.

#### Scenario: A worker silo dies holding permits

- **WHEN** a silo holding heavy-work permits is killed
- **THEN** its permits are released once the cluster has declared it dead or the lease has lapsed,
  and the bound is whole again

#### Scenario: Interactive commands during a heavy burst

- **WHEN** interactive commands are dispatched while the heavy pool is saturated
- **THEN** their latency stays within its no-burst range — verified with a saturated heavy pool on
  the PostgreSQL store

#### Scenario: Two heavy handlers compute without yielding

- **WHEN** two heavy commands are dispatched to one silo and both handlers compute for longer than
  the grace without awaiting anything
- **THEN** both run at the same time, each runs once, and neither is handed over a second time while
  it runs — verified on the PostgreSQL store

#### Scenario: The silo keeping the permits dies while the bound is full

- **WHEN** the silo on which the permits are kept is killed while units on another silo hold every
  permit, and further heavy commands are dispatched
- **THEN** at no moment do more units than the bound run, the running units are counted again within
  a lease of the keeper's return, and the further commands start only once the grace has passed and
  a running unit has ended — verified with two silos on the PostgreSQL membership table and the Redis
  directory

#### Scenario: A running unit is refused when it registers again

- **WHEN** a running unit registers with a returned keeper after its lease had already lapsed and the
  bound is full
- **THEN** it runs to its end, an event names the unit, and it holds a permit again as soon as one is
  free

#### Scenario: A permit falls free while a refused unit and a new command both want it

- **WHEN** a permit is released while a running unit that was refused is asking for one and a heavy
  command is waiting to start
- **THEN** the running unit holds the permit and the waiting command keeps waiting

### Requirement: A host can reset what the execution model keeps outside the event stream

A host SHALL be able to clear, deterministically, everything the execution model keeps beside the
event stream for its own deployment — the reminders of its service, the membership of its cluster, the
grain directory's entries and the checkpoints of the store-reading projections and sagas the host
registers — so that the deployment can be brought back to "nothing scheduled, nothing remembered". A
reset SHALL NOT remove a reminder, membership row or checkpoint that belongs to another deployment or
to a consumer the host does not register, and SHALL report how many of each it removed. A reset offered
by a host that goes on running — the test-support host is the one the framework ships — SHALL instead
leave every registered reader where the store's head is and report how many it moved, because a reader
returned to the beginning would apply the store again into read models the reset does not empty. The
event stream SHALL NOT be touched by a reset. A host SHALL be able to name the schema its runtime tables
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
documentation SHALL state this where a read store is configured for the execution model. A reader
that advances a checkpoint SHALL advance it only from the position it last saw: a write that finds
another position SHALL be refused with a message naming the position found and the one expected,
and the refused reader SHALL read the checkpoint again rather than trust its own, so that an
activation that outlived its successor never rewinds or overtakes what the successor wrote. A
checkpoint SHALL NOT be written under another reader's name than the one it holds — advancing or
replacing it — and such a write SHALL be refused with a message naming both readers, as a read under
another reader is; a host switching readers resets its checkpoints first, as documented.

#### Scenario: Two deployments share a read store with distinct projections

- **WHEN** two deployments register store-reading projections with different names against one read
  store and both run
- **THEN** each deployment's projections advance their own checkpoints, and neither deployment's
  reading or reset changes the other's

#### Scenario: Two deployments share a read store and both run store-reading sagas

- **WHEN** two deployments that share a read store both run store-reading sagas
- **THEN** the documentation names this as unsupported, because both write the same checkpoints

#### Scenario: A stale activation writes a checkpoint

- **WHEN** an activation that last saw a checkpoint at one position writes after another activation
  of the same reader has advanced it
- **THEN** the write is refused naming both positions, the successor's checkpoint stands, and the
  refused activation reads the checkpoint again before it reads the store again

#### Scenario: A checkpoint is written under another reader's name

- **WHEN** a checkpoint written by one reader is advanced or replaced under another reader's name
- **THEN** the write is refused with a message naming both readers, and the checkpoint is unchanged

### Requirement: A host can start its store readers at the head of a populated store

A host whose read models are current when it adopts the execution model SHALL be able to seed, once
and while no silo of its cluster runs, a checkpoint at the store's current head for every
store-reading projection and saga it registers and every partition where none exists, so that its
first start applies only what commits afterwards. Seeding SHALL NOT change a checkpoint that exists,
SHALL write under the name of the reader the host registers, and SHALL report how many checkpoints it
wrote and how many it left. A consumer registered later without a checkpoint SHALL still start at the
beginning of the store. The documentation SHALL name seeding as the step between migrating the schema
and the first start on a populated store, and SHALL state that a checkpoint is keyed by the consumer's
name, so that renaming a projection starts it at the beginning.

#### Scenario: A populated store is seeded before the first start

- **WHEN** a host with a populated event store and current read models seeds its checkpoints and then
  starts for the first time
- **THEN** no entry committed before the seeding is applied by any of its projections or sagas, and
  every entry committed afterwards is — verified on the PostgreSQL store with the native reader

#### Scenario: A consumer already has a checkpoint

- **WHEN** seeding runs on a read store in which one registered consumer already has checkpoints
- **THEN** those checkpoints keep their positions, the other consumers are seeded, and the report
  counts both

#### Scenario: A projection is added after the seeding

- **WHEN** a host registers a new projection after its checkpoints were seeded and starts
- **THEN** the new projection reads from the beginning of the store, and the seeded ones from their
  checkpoints

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
