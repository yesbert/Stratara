## MODIFIED Requirements

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
registered SHALL apply whatever order it and the execution model were registered in.

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
rebuild requested while a full replay is active SHALL be refused with a message that says so. A
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
