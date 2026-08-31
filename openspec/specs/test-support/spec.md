# test-support Specification

## Purpose
Let a consumer test against the framework's real behaviour without a database, a broker or a key
management service — so that the thing under test is their own code and not a mock of ours.

## Requirements

### Requirement: An aggregate can be rebuilt from events in a test without any infrastructure

The framework SHALL offer a harness that builds an aggregate from a list of events, in order,
through the same apply-dispatch the production rebuild uses.

Dispatching through a different mechanism than production would make a passing test evidence of
nothing.

#### Scenario: Events are supplied

- **WHEN** a harness is given events and asked to build
- **THEN** the aggregate reflects them, applied in the order given

#### Scenario: No events are supplied

- **WHEN** a harness is asked to build with no events
- **THEN** a fresh aggregate is returned

#### Scenario: Events are added in stages

- **WHEN** further events are added after the initial ones
- **THEN** they are applied after them, preserving order

#### Scenario: A shortcut is used

- **WHEN** the single-call shortcut is used instead of the harness
- **THEN** the result is the same as the harness produces

### Requirement: The harness dispatches exactly as production does, including the version

The harness SHALL invoke a handler taking the event payload, or one taking the enveloped event,
under the same preference production uses. Where it invokes an enveloped handler, the envelope SHALL
carry the event's version, counted from one in the order supplied.

#### Scenario: The aggregate declares a payload handler

- **WHEN** the aggregate declares a handler taking the event payload
- **THEN** it is invoked with the payload

#### Scenario: The aggregate declares an enveloped handler

- **WHEN** the aggregate declares a handler taking the enveloped event
- **THEN** it is invoked, and the envelope carries the payload and the event's position as its
  version

### Requirement: An unmapped event fails the test by default

Where an aggregate declares no handler for a supplied event, the harness SHALL fail by default, and
SHALL skip it only when the test asks it to.

Production skips an unhandled event silently, which is right in production and wrong in a test: a
handler whose parameter type is wrong is exactly the defect a test should catch, and silence is what
lets it through.

#### Scenario: An event has no matching handler

- **WHEN** an event is supplied that the aggregate declares no handler for
- **THEN** the harness fails, rather than quietly skipping it

#### Scenario: The test opts into skipping

- **WHEN** the test declares that unmapped events should be ignored
- **THEN** they are skipped and the remaining events applied

### Requirement: A test can exercise the real event store on an in-memory database

The framework SHALL offer a host that wires the framework's own event source, write store,
aggregation and snapshot components against an in-memory database, so a test exercises production
code paths rather than substitutes.

#### Scenario: A stream is created, appended to and rebuilt

- **WHEN** a test creates a stream, appends to it and rebuilds the aggregate through the host
- **THEN** it goes through the framework's real event source, write store and aggregation

#### Scenario: Several saves are made

- **WHEN** appends are made across separate save operations
- **THEN** they persist, because the host holds the in-memory database open for its lifetime rather
  than per connection

#### Scenario: A stream is created twice

- **WHEN** a stream that already exists is created again through the host
- **THEN** it fails exactly as it would in production

#### Scenario: An unknown stream is rebuilt

- **WHEN** an aggregate is requested for a stream that does not exist
- **THEN** nothing is returned

#### Scenario: A stream crosses the snapshot threshold

- **WHEN** enough events are appended for the snapshot policy to fire
- **THEN** rebuilding still yields the correct state, having gone through the real snapshot path

#### Scenario: Two aggregate types share a stream

- **WHEN** two aggregate types are rebuilt from one stream, each with its own snapshots
- **THEN** each rebuilds from its own snapshot, and a snapshot belonging to the other type does not
  corrupt the load

### Requirement: The host makes what happened observable

The host SHALL expose the session it operates under, allow the acting tenant to be changed, and
record the event bundles a save produced, so a test can assert on what was published without a
broker.

#### Scenario: A save publishes a bundle

- **WHEN** a save persists events through the host
- **THEN** the resulting bundle is recorded and available for assertion

#### Scenario: A test switches tenant

- **WHEN** a test changes the session's tenant
- **THEN** subsequent operations run under it

### Requirement: In-memory doubles honour the contracts they stand in for

The framework SHALL offer in-memory implementations of the key store, the message bus, the session
provider, the membership store, the settings store, the API-key store and the blob encryptor, and
each SHALL honour the guarantees its contract states rather than being a stub.

A double that quietly ignores a guarantee turns every test that relies on it into a false pass.

#### Scenario: The key store double is used

- **WHEN** a test rotates, revokes or erases through the in-memory key store
- **THEN** rotation keeps earlier versions resolvable, revocation shreds one version, erasure shreds
  all of them, distinct scopes get distinct keys, and a returned key buffer is a copy

#### Scenario: The membership store double is used

- **WHEN** a test uses the in-memory membership store
- **THEN** forward and reverse lookups agree, writing is an upsert, active-tenant selection is
  membership-guarded, and the erasure sweeps clear selections along with memberships

#### Scenario: The API-key store double is used

- **WHEN** a test issues, validates, imports or revokes through the in-memory API-key store
- **THEN** expiry, revocation, the canonical-format check, the never-mutating import and the
  membership materialisation all behave as the real store does

#### Scenario: The settings store double is used

- **WHEN** a test writes and reads through the in-memory settings store
- **THEN** storage is per exact scope, writing an absent value deletes, and the erasure sweeps
  clear by dimension

#### Scenario: The message bus double is used

- **WHEN** a test publishes through the in-memory bus
- **THEN** a matching subscriber receives it, a subscriber for a different message type on the same
  topic does not, and every message is recorded for assertion whether or not anything was listening

#### Scenario: The message bus double stands in for a subscription established early

- **WHEN** a test establishes a subscription, publishes, and only then attaches a handler
- **THEN** the handler receives what was published in between, so a test cannot pass on start-up
  ordering the real broker would fail

#### Scenario: The blob encryptor double is used

- **WHEN** a test encrypts and decrypts through the test encryptor
- **THEN** the round trip recovers the plaintext and decrypting under a different scope fails, so a
  test cannot pass on scope binding the real encryptor would refuse

### Requirement: Test identities are deterministic and derived from a name

The framework SHALL derive a stable identifier from a readable name, so tests can refer to tenants
by name and get the same identifier on every run and in every test.

#### Scenario: The same name is used twice

- **WHEN** an identifier is derived from the same name in two places
- **THEN** the identifiers are equal

#### Scenario: A blank name is used

- **WHEN** a blank name is supplied
- **THEN** it is rejected rather than producing a shared identifier for every blank

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
