# test-support

## MODIFIED Requirements

### Requirement: A test can run the execution model in one process

The framework SHALL offer a host that runs the Orleans execution model in the test's own process,
without a cluster, a broker or a database server, so that a test exercises a consumer's handlers,
projections, sagas and timers through the same registrations and the same grains as production. Every
period the execution model keeps as a reminder or a poll SHALL be short enough on the host that a
projection applies and a timer fires within seconds, and a test SHALL be able to shorten or lengthen
them. The host SHALL expose the session it operates under, the timers, the seeding and the reset the
execution model offers, and a wait that returns once every registered store reader has reached the
store's head, so that a test asserts on a read model without polling it.

The host's reset SHALL be usable while the host runs, because that is where a test between two tests
uses it: it SHALL leave every registered store reader reading on from what the store holds at that
moment, so that the next test's facts are applied, the last test's are not applied again, and the wait
for the readers returns. A start that fails SHALL leave nothing of the host running.

A host that runs alone in memory is one silo: the host SHALL say so, and a test of what happens
across silos belongs to an integration test against real infrastructure.

#### Scenario: A command runs in its aggregate's activation and a projection applies it

- **WHEN** a test registers its handlers and projections on the host as it does in production,
  dispatches a command naming an aggregate, and waits for the readers
- **THEN** the handler ran in the aggregate's activation, the projection applied the committed facts,
  and the wait returned within seconds

#### Scenario: A timer fires within seconds

- **WHEN** a test registers a timer due in a second for an owner the host's owner check knows
- **THEN** the handler receives it within a few seconds, and the timer is gone afterwards

#### Scenario: A process timeout fires

- **WHEN** a test's stateful process schedules a timeout in a step and the fact is applied through
  the host's saga role
- **THEN** the timeout reaches the process within seconds, with the state as recorded

#### Scenario: A test seeds and resets

- **WHEN** a test appends facts, seeds the host's readers at the head, registers a projection and
  starts it
- **THEN** the projection applies nothing appended before the seeding and everything after; and after a
  reset the host remembers no timer and every reader is where the store's head is

#### Scenario: A host's start fails

- **WHEN** the host cannot start — what a test asked to run before the start throws
- **THEN** the test is given that failure, and nothing the host had started keeps running

#### Scenario: A host is reset between two tests

- **WHEN** a test resets a running host after facts were applied, and then dispatches again
- **THEN** the wait for the readers returns, what the next dispatch commits is applied, and nothing
  applied before the reset is applied a second time

#### Scenario: A test shortens a period below the runtime's default

- **WHEN** a test sets the host's poll interval, keep-alive or retry period to one second
- **THEN** the host starts, with the shortened periods in force
