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
without an intent store SHALL report it. A resumption the drain holds back because a full replay is
active SHALL be logged when the holding back begins and when it ends, so that a recorded command
that waits for the length of a replay is seen waiting rather than lost.

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

### Requirement: Work that must happen once happens once per cluster

Singleton work SHALL run in one place in the cluster at its period, without a lock, only on a silo
that registered it, and SHALL resume elsewhere when the silo running it is lost. Owner-checked durable
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
