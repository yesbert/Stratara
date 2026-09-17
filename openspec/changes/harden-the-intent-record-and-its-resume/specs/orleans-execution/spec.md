## MODIFIED Requirements

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
