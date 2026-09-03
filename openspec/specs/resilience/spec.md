# resilience Specification

## Purpose
Give every transient-failure retry in the framework, and in a consumer's own handlers, one named
and configurable policy — so that retry behaviour is a property of the system that can be inspected
and changed, rather than a loop written differently in every call site.

## Requirements

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

### Requirement: Message-bus traffic retries indefinitely behind a circuit breaker

The message-bus policy SHALL retry indefinitely with exponential, jittered backoff bounded by a
maximum delay, behind a circuit breaker. The circuit SHALL open under sustained failure and SHALL
close again once the broker recovers.

A broker outage is expected to end, so giving up would discard work that will succeed shortly. The
circuit breaker is what makes a *permanent* failure distinguishable from a passing one: without it,
an outage of ten minutes and an outage of ten seconds look the same to an operator, differing only
in how long the retries continue.

The breaker's counting window SHALL be wide enough for the retry's own maximum delay to fill it. A
window narrower than the delay admits fewer failures than the breaker requires, which leaves the
breaker unable to open at all — stated here because that state is invisible: nothing fails, no test
breaks, and the only symptom is an alert that never fires.

#### Scenario: The broker is unavailable and recovers

- **WHEN** message-bus operations fail while the broker is down and the broker later recovers
- **THEN** the operation eventually succeeds without the caller having implemented any retry

#### Scenario: The broker stays unavailable

- **WHEN** message-bus operations fail continuously for longer than the breaker's counting window
- **THEN** the circuit opens, and that is observable to the host

#### Scenario: The circuit is open and the broker returns

- **WHEN** the broker recovers while the circuit is open
- **THEN** the circuit closes again and traffic resumes, without the caller intervening

#### Scenario: The retry's maximum delay is changed

- **WHEN** the retry's maximum delay is raised so that fewer failures fall inside the breaker's
  counting window than the breaker requires to open
- **THEN** that is a defect the framework's own tests detect, rather than a silently inert breaker

### Requirement: Dispatch policies retry a bounded number of times

The command-dispatch and event-bundle-dispatch policies SHALL retry a small, bounded number of
times with exponential, jittered backoff, and then surface the failure.

Unlike bus traffic, a dispatch failure has a fallback — the outbox — so retrying forever would keep
work in memory that belongs in durable storage.

#### Scenario: Dispatch keeps failing

- **WHEN** a dispatch fails on every attempt
- **THEN** the policy stops after its bounded attempts and the failure reaches the caller, which can
  fall back to durable storage

### Requirement: The concurrency-conflict policy retries only concurrency conflicts

The concurrency-conflict policy SHALL retry only optimistic-concurrency conflicts, and SHALL let
every other failure through on the first occurrence.

A policy that retried indiscriminately would re-run handlers after failures that will never
succeed, and would mask real defects as slowness.

#### Scenario: An append conflicts with a concurrent writer

- **WHEN** an operation fails with an optimistic-concurrency conflict
- **THEN** it is retried, with backoff, up to the policy's attempt limit

#### Scenario: An operation fails for any other reason

- **WHEN** an operation fails with anything other than an optimistic-concurrency conflict
- **THEN** it is not retried and the failure surfaces immediately

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

### Requirement: The replay-batch policy retries a bounded number of times over a short window

The projection-replay-batch policy SHALL retry a failing operation a small, bounded number of times
with exponential, jittered backoff whose waits between attempts add up to several seconds, and
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

### Requirement: A request opts in to a policy and names which one

A request SHALL be run under a resilience policy only when it declares itself resilient and names
the policy it wants. A request that does not declare it SHALL pass through the resilience stage
untouched, with no policy lookup.

Naming the policy on the request rather than configuring it centrally is what lets one host use the
conflict-retry policy for the handful of handlers that need it, without wrapping every request.

#### Scenario: A request declares itself resilient

- **WHEN** a request implements the resilient-request marker and names a registered policy
- **THEN** its in-process dispatch runs inside that policy, so a retry re-invokes the handler

#### Scenario: A request does not declare itself resilient

- **WHEN** a request does not implement the marker
- **THEN** it is dispatched directly and no policy is resolved

### Requirement: Retrying re-runs the handler, so only safe handlers may opt in

Where a request is retried, the framework SHALL re-invoke the handler itself, not a cached result.
A request may therefore declare itself resilient only where re-running its handler is safe — either
because the handler is idempotent or because it is guarded by optimistic concurrency.

This is stated as a requirement rather than left implicit because the failure mode is a duplicated
side effect, which is invisible on the success path and irreversible on the failure path.

#### Scenario: A handler runs twice

- **WHEN** a resilient request's first attempt fails in a way its policy retries
- **THEN** the handler runs again from the beginning

### Requirement: The resilience stage runs inside the guard stages

The resilience stage SHALL be registered after validation and tenant isolation, so that a retry
re-runs the handler and not the guards.

Re-running the guards would be wasted work at best; at worst it would re-resolve permissions
mid-retry and produce a different decision than the one the request was admitted under.

#### Scenario: A guarded, resilient request is retried

- **WHEN** a request passes validation and tenant isolation and is then retried by its policy
- **THEN** the retry re-runs only the handler
