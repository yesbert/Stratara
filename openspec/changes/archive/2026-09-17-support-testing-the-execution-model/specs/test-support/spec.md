## ADDED Requirements

### Requirement: A test can run the execution model in one process

The framework SHALL offer a host that runs the Orleans execution model in the test's own process —
one silo clustered with itself, reminders and the grain directory in memory, the framework's real
write stack, commit-order reader and checkpoint store on an in-memory database — so that a test
exercises a consumer's handlers, projections, sagas and timers through the same registrations and
the same grains as production, without a cluster, a broker or a database server. Every period the
execution model keeps as a reminder or a poll SHALL be short enough on the host that a projection
applies and a timer fires within seconds, and a test SHALL be able to shorten or lengthen them. The
host SHALL expose the session it operates under, the timers, the seeding and the reset the execution
model offers, and a wait that returns once every registered store reader has reached the store's
head, so that a test asserts on a read model without polling it.

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
  reset the host remembers no checkpoint and no timer

#### Scenario: A test shortens a period below the runtime's default

- **WHEN** a test sets the host's poll interval, keep-alive or retry period to one second
- **THEN** the host starts, because it lowers the runtime's minimum reminder period with them

## MODIFIED Requirements

### Requirement: Test-support packages are for test projects

The test-support packages SHALL be published and versioned like the rest of the family, and SHALL be
referenced only from test projects — they wire in-memory and development-grade implementations that
must never reach a running system. The framework SHALL enforce that boundary at build time, and the
test-support event-store composition and the test-support execution-model host SHALL additionally
refuse to register into a host that reports an environment other than development.

The runtime guard is deliberately not a whitelist. A test ordinarily runs with no host and no stated
environment at all, and refusing that case would refuse the only legitimate use. The composition is
therefore refused only when something states an environment and that environment is not development.

#### Scenario: A consumer references a test-support package

- **WHEN** a consumer references a test-support package from a test project
- **THEN** it resolves at the same lockstep version as every other package

#### Scenario: A test-support package reaches production code

- **WHEN** a project that is not a test project references a test-support package
- **THEN** the build fails, naming the referencing project and the package
- **AND** a consumer with a deliberate exception can suppress the failure through a documented
  opt-out property

#### Scenario: The build-time check cannot see the reference

- **WHEN** a test-support package is consumed other than as a package reference — as a project
  reference within a single solution, for example
- **THEN** the build-time check does not fire, and the runtime guard is the only remaining defence

#### Scenario: The test event-store composition is wired into a host

- **WHEN** the test event-store composition is registered and the host states an environment other
  than development
- **THEN** registration fails, naming the environment it found and how to register the real store

#### Scenario: The test event-store composition is used in a test

- **WHEN** the test event-store composition is registered and no environment is stated anywhere
- **THEN** it registers and works — the ordinary unit-test case, which the guard does not disturb

#### Scenario: The test execution-model host is created under a stated environment

- **WHEN** the test execution-model host is created while an environment other than development is
  stated
- **THEN** creation fails, naming the environment it found and that the host runs the execution
  model in memory

#### Scenario: An in-memory double is constructed directly

- **WHEN** an in-memory double is constructed by hand rather than through the test event-store
  composition
- **THEN** no environment guard applies to it — the build-time check is the only defence, and where
  it cannot see the reference there is none
