## MODIFIED Requirements

### Requirement: The framework registers named resilience policies

Registering resilience SHALL make a fixed set of named policies available, addressable by published
name constants: one for message-bus traffic, one for command dispatch, one for event-bundle
dispatch, one for optimistic-concurrency conflicts and one for a missing prerequisite.

#### Scenario: Resilience is registered

- **WHEN** a host registers the framework's resilience policies
- **THEN** all five named policies are resolvable by their published names

#### Scenario: Registration happens more than once

- **WHEN** the registration is performed twice
- **THEN** the result is the same as registering once — no policy is duplicated or replaced

#### Scenario: Two named policies are resolved

- **WHEN** two different policy names are resolved
- **THEN** they are separate policies, so a circuit opened by one does not affect the other

## ADDED Requirements

### Requirement: The missing-prerequisite policy retries only a missing prerequisite, briefly

The missing-prerequisite policy SHALL retry only the report that a fact's prerequisite has not been
applied yet, a bounded number of times with a short exponential backoff that completes within a few
seconds in total, and SHALL let every other failure through on the first occurrence.

The window is deliberately short. It covers a prerequisite that is in flight on another consumer of
the same process — a matter of milliseconds — and not one whose consumer has failed; that case is
what replay exists for, and a policy that waited for it would hold a consumer for as long as it took.

#### Scenario: A handler reports a missing prerequisite

- **WHEN** an operation fails with the missing-prerequisite report
- **THEN** it is retried, with backoff, up to the policy's attempt limit

#### Scenario: An operation fails for any other reason

- **WHEN** an operation fails with anything other than the missing-prerequisite report
- **THEN** it is not retried and the failure surfaces immediately

#### Scenario: The attempts are exhausted

- **WHEN** every attempt reports a missing prerequisite
- **THEN** the last report surfaces to the caller unchanged, so the caller can record what was missing
