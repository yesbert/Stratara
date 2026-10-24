## ADDED Requirements

### Requirement: A keep-alive is answered while its grain is busy

Where a host has registered the Orleans execution model, the keep-alive of a store reader of a projection or a
saga, and the keep-alive of a singleton work, SHALL be answered within the runtime's response timeout whatever
the reader or the work is doing. A reader that catches up longer than the response timeout, and a singleton work
whose run lasts longer than it, SHALL therefore leave no undelivered reminder tick in the silo's log. A silo that
starts while a reader or a work is busy in this way SHALL start without waiting for it. Answering
a keep-alive SHALL NOT change what the reader or the work does: a partition SHALL still be read by one catch-up
at a time, a keep-alive or a poll that arrives during a catch-up SHALL NOT start a second one, a fact committed
during a catch-up SHALL be applied by it or by the next, and a singleton work's run SHALL still never overlap with
another run of the same work. A reader or a work that is being deactivated MAY still leave a tick undelivered
while it waits for its running catch-up or run. The documentation SHALL
say that a durable timer whose handler outlasts the response timeout still makes the runtime log an undelivered
reminder tick for that tick, and that the line says nothing about the timer, which fires once as before.

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

#### Scenario: The documentation names the reminder line a durable timer still leaves

- **WHEN** an operator reads the operating guide's reminder profile
- **THEN** it says that store readers and singleton work answer their keep-alive while busy, and that a durable
  timer whose handler outlasts the response timeout makes the runtime log an undelivered reminder tick that says
  nothing about the timer
