## ADDED Requirements

### Requirement: A reminder tick is answered while its grain is busy

Where a host has registered the Orleans execution model, the keep-alive of a store reader of a projection or a
saga, and the keep-alive of a singleton work, SHALL be answered within the runtime's response timeout whatever
the reader or the work is doing. A reader that catches up longer than the response timeout, and a singleton work
whose run lasts longer than it, SHALL therefore leave no undelivered reminder tick in the silo's log. A silo that
starts while a reader or a work is busy in this way SHALL start without waiting for it. Answering
a keep-alive SHALL NOT change what the reader or the work does: a partition SHALL still be read by one catch-up
at a time, a keep-alive or a poll that arrives during a catch-up SHALL NOT start a second one, a fact committed
during a catch-up SHALL be applied by it or by the next, and a singleton work's run SHALL still never overlap with
another run of the same work. A reader or a work that is being deactivated MAY still leave a tick undelivered
while it waits for its running catch-up or run.

The reminder tick of a durable timer SHALL likewise be answered within the runtime's response timeout whatever the
timer's handler does, so that a handler that runs longer than the response timeout leaves no undelivered reminder
tick in the silo's log. Answering the tick SHALL NOT change what the timer guarantees: it SHALL fire once per due
time, a tick that arrives while its handler runs SHALL NOT start the handler again, the timer SHALL be unregistered
only once its handler has completed, a handler stopped with its silo SHALL leave the timer registered to fire on the
next silo, and a timer registered for the same owner and purpose from its own handler SHALL be kept. A handler that
fails, other than with the failure saying its events were committed but could not be published, SHALL be logged at
error level with an event of the framework's own that names the owner and the purpose and carries the failure, and
the timer SHALL stay registered and fire again after its retry period. A timer owner that is being deactivated MAY
still leave a tick undelivered while it waits for a running handler. The documentation SHALL say that store readers,
singleton work and durable timers answer their reminder tick while busy, and SHALL name the event a failing timer
handler logs.

#### Scenario: A projection catches up longer than the response timeout

- **WHEN** a projection's partition has a backlog whose catch-up lasts several keep-alive periods and longer than
  the runtime's response timeout
- **THEN** the projection applies the backlog in commit order, its checkpoint advances to the end, and the silo
  logs no undelivered reminder tick and no response that did not arrive on time for the projection's readers.
  Verified with a shortened response timeout and a shortened keep-alive period on the PostgreSQL store

#### Scenario: A saga reader catches up longer than the response timeout

- **WHEN** a saga's reader catches up a backlog that lasts longer than the runtime's response timeout and its
  keep-alive period
- **THEN** the saga handles every entry once, in commit order, and the silo logs no undelivered reminder tick for
  the saga's readers

#### Scenario: A keep-alive during a catch-up starts no second one

- **WHEN** a fact commits in a partition while its reader is catching up, and the reader's keep-alive and poll
  arrive during that catch-up
- **THEN** the keep-alive is answered at once, no second catch-up runs beside the first, and the fact is applied
  once

#### Scenario: A singleton work runs longer than the response timeout

- **WHEN** a singleton work's run lasts longer than the runtime's response timeout and its keep-alive period
- **THEN** the run ends once, the next run starts only after it, and the silo logs no undelivered reminder tick
  for the work. Verified with a shortened response timeout and a shortened keep-alive period on the PostgreSQL
  store

#### Scenario: A silo starts while a singleton work runs long

- **WHEN** a second silo that registers the same singleton work starts while the work's run on the first silo
  lasts longer than the runtime's response timeout
- **THEN** the second silo starts, the run is not interrupted and does not run a second time, and the work keeps
  running in one place. Verified where both silos run this release

#### Scenario: A timer's handler runs longer than the response timeout

- **WHEN** a timer's handler runs longer than the runtime's response timeout and its retry period
- **THEN** the handler runs once, the timer is unregistered after it has completed, and the silo logs no undelivered
  reminder tick for the timer's owner. Verified with a shortened response timeout and retry period on the PostgreSQL
  store

#### Scenario: A timer's handler fails

- **WHEN** a timer's handler throws on its first attempt and returns on its second
- **THEN** the failure is logged once at error level with the owner and the purpose, the timer fires again after its
  retry period, and it is unregistered once the second attempt has completed

#### Scenario: The documentation names what answers its reminder tick while busy

- **WHEN** an operator reads the operating guide's reminder profile
- **THEN** it says that store readers, singleton work and durable timers answer their reminder tick while busy, and
  names the event a failing timer handler logs

## REMOVED Requirements

### Requirement: A keep-alive is answered while its grain is busy

**Reason**: Folded into *A reminder tick is answered while its grain is busy*, which keeps every guarantee and
scenario it gave store readers and singleton work, extends the same guarantee to durable timers, and drops the
clause that the documentation names the reminder line a long timer handler left, because that line no longer
appears.

**Migration**: None. Store readers and singleton work behave as before.
