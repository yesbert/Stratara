## ADDED Requirements

### Requirement: A recorded command that meets a concurrency conflict runs again at once

Where a host has registered the Orleans execution model's command dispatcher, a recorded command whose handler fails
on a concurrency conflict SHALL run again at once on the silo that ran it, in a unit of work of its own, after a
short pause that grows with every conflict, while its hand-over is still held, so that a command that collides with
other work on the same stream completes within moments of that work rather than after the grace. A heavy command
SHALL keep its permit while it runs again. Every conflict SHALL be recorded and counted against the conflict bound as
before, and every attempt that fails SHALL be logged as before. The command SHALL run again in this way only while
its recorded conflicts leave room for another run under the conflict bound, and no more often than the framework's
concurrency-conflict pipeline retries. After that it SHALL be resumed by the drain as before, so that a command that
conflicts on every attempt runs as often in all as it did before and is then kept with the conflict as its last
failure. A command recorded with a store that cannot tell how many conflicts a command has met SHALL be left to the
drain after a conflict, as before. A forwarded command that is not recorded SHALL hand its conflict back to its
caller, as before. The documentation SHALL say how a conflict is handled on the execution model.

#### Scenario: A command's first attempt meets a conflict

- **WHEN** a recorded command's handler meets a concurrency conflict on its first attempt because another writer
  appended to the stream after the handler read it, and finds no conflict on its second
- **THEN** the command runs again within a second, in a unit of work other than the first attempt's, and succeeds;
  the record holds one conflict, and the drain does not run it again. Verified on the PostgreSQL store with a handler
  that reports the event store's conflict on its first attempt

#### Scenario: A command meets a conflict on every attempt

- **WHEN** a recorded command's handler fails on a concurrency conflict on every attempt
- **THEN** it runs as often in all as the conflict bound allows and one more time, as before, and is then kept with
  the conflict as its last failure. Verified on the PostgreSQL store

#### Scenario: A heavy command's first attempt meets a conflict

- **WHEN** a heavy command's handler meets a concurrency conflict on its first attempt and none on its second
- **THEN** it runs again within a second without giving up its permit, and succeeds. Verified at the command's
  execution, with the permit's hold counted around both attempts

#### Scenario: The store cannot count conflicts

- **WHEN** a command is recorded with an intent store that does not tell how many conflicts a command has met, and its
  handler meets a conflict
- **THEN** the conflict is recorded as before and the command is left to the drain

#### Scenario: The documentation says what a conflict does

- **WHEN** an operator reads the operating guide for the execution model
- **THEN** it says that a conflicting command runs again at once on its silo, within the conflict bound and the
  framework's concurrency-conflict pipeline, and is left to the drain after that
