## ADDED Requirements

### Requirement: Waiting costs a recorded command no attempt

Where a host has registered the Orleans execution model's command dispatcher with an intent store, a recorded command
SHALL NOT use up an attempt of its delivery bound by waiting: not for a worker, not for a permit, not behind other
commands, and not for room in the heavy-work pool. A hand-over that a silo refuses because its heavy-work pool already
holds as many commands as the host allows, a hand-over that finds its command still held from an earlier hand-over,
and a hand-over whose command a stopping silo ends before its handler completes SHALL each give back the attempt it
counted. A refused command SHALL stay recorded and SHALL be handed over again later. The heavy-work pool SHALL hold
no more accepted commands, running and waiting together, than a bound the host configures. Keeping held commands out
of resumption SHALL cost the store a number of statements per renewal period that does not grow with the number of
commands a silo holds. A command whose handler runs and fails SHALL still count against the delivery bound as before,
and SHALL still be kept once it reaches it.

The operations documentation SHALL say how a burst of heavy commands behaves, which settings bound it, and how they
relate to the connections a silo has.

#### Scenario: A burst of heavy commands many times the cluster-wide limit

- **WHEN** many times more heavy commands are dispatched than the cluster-wide limit, the pool holds fewer than are
  dispatched, and each runs longer than the grace's renewal period
- **THEN** every command completes, none is kept, and no record's attempt count exceeds one at any time — verified
  on the PostgreSQL store

#### Scenario: Renewals while a burst waits

- **WHEN** a silo holds a thousand commands, running or waiting
- **THEN** keeping them out of resumption costs the store a number of statements per renewal period that is the same
  for ten commands as for a thousand, up to the renewal batch size

#### Scenario: A hand-over finds the pool full

- **WHEN** a heavy command is handed over to a pool that already holds as many commands as the host allows
- **THEN** its handler does not run, its attempt is given back, and it is handed over again after the grace and runs

#### Scenario: A hand-over finds its command still held

- **WHEN** a recorded command's record lapses while the command waits on a silo, and the drain hands it over again
  to the silo that holds it
- **THEN** the second hand-over is dropped, its attempt is given back, and the command runs once

#### Scenario: A silo is stopped and started during a burst

- **WHEN** a silo holding waiting heavy commands is stopped and another is started
- **THEN** the waiting commands are resumed on the other silo, no record's attempt count exceeds one, and every command
  completes — verified on the PostgreSQL store

#### Scenario: A handler keeps failing

- **WHEN** a heavy command's handler fails on every attempt
- **THEN** it is kept once its attempts reach the delivery bound, as before
