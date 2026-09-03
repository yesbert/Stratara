## MODIFIED Requirements

### Requirement: The framework registers named resilience policies

Registering resilience SHALL make a fixed set of named policies available, addressable by published
name constants: one for message-bus traffic, one for command dispatch, one for event-bundle
dispatch, one for optimistic-concurrency conflicts, one for a missing prerequisite and one for a
projection-replay batch.

#### Scenario: Resilience is registered

- **WHEN** a host registers the framework's resilience policies
- **THEN** all six named policies are resolvable by their published names

#### Scenario: Registration happens more than once

- **WHEN** the registration is performed twice
- **THEN** the result is the same as registering once — no policy is duplicated or replaced

#### Scenario: Two named policies are resolved

- **WHEN** two different policy names are resolved
- **THEN** they are separate policies, so a circuit opened by one does not affect the other

## ADDED Requirements

### Requirement: The replay-batch policy retries a bounded number of times over a short window

The projection-replay-batch policy SHALL retry a failing operation a small, bounded number of times
with exponential, jittered backoff whose attempts together span on the order of half a minute, and
SHALL then surface the failure. It SHALL retry any failure except cancellation.

The window is longer than the dispatch policies' because what it waits for is a read store under
load recovering, not a broker accepting a message: a timeout that resolves in seconds is the case
it exists for. It is bounded because a replay that retried indefinitely could not be told apart from
one that is hung, and the operator watching it needs it to end.

#### Scenario: An operation fails and then succeeds

- **WHEN** an operation fails on the first attempt and succeeds within the attempt limit
- **THEN** the policy returns the successful result

#### Scenario: An operation keeps failing

- **WHEN** an operation fails on every attempt
- **THEN** the policy stops after its bounded attempts and the last failure reaches the caller
  unchanged

#### Scenario: The operation is cancelled

- **WHEN** the operation is cancelled during an attempt
- **THEN** the policy does not retry and the cancellation reaches the caller
