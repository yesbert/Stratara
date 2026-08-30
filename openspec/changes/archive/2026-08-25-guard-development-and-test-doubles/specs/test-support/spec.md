## MODIFIED Requirements

### Requirement: Test-support packages are for test projects

The test-support packages SHALL be published and versioned like the rest of the family, and SHALL be
referenced only from test projects — they wire in-memory and development-grade implementations that
must never reach a running system. The framework SHALL enforce that boundary at build time, and the
test-support event-store composition SHALL additionally refuse to register into a host that reports
an environment other than development.

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

#### Scenario: An in-memory double is constructed directly

- **WHEN** an in-memory double is constructed by hand rather than through the test event-store
  composition
- **THEN** no environment guard applies to it — the build-time check is the only defence, and where
  it cannot see the reference there is none
