# request-validation Specification

## Purpose
Give consumers one place to state what makes a request acceptable, so that an invalid command
never reaches domain logic and every rejection reaches the caller in the same shape — without the
framework's default path depending on any third-party validation library.

## Requirements

### Requirement: Vendor-neutral validation contract

The framework SHALL define the validation contract in the abstractions package, with no dependency
on any third-party validation library, so that a consumer can implement a validator, and catch a
validation failure, without referencing the package that performs validation.

#### Scenario: A consumer implements a validator against abstractions alone

- **WHEN** a consumer references only the abstractions package
- **THEN** the validator contract, the result and failure shapes, the severity levels and the
  validation exception are all available to it

#### Scenario: A consumer maps failures to its own error model

- **WHEN** a request is rejected and the consumer's global exception handler catches the
  validation exception
- **THEN** the exception exposes the aggregated blocking failures, each carrying the property
  name, a human-readable message, an optional machine-readable code and the rejected value
- **AND** the handler needs no reference to the package that threw

### Requirement: A validator reports failures rather than throwing

A validator SHALL return a result describing the failures it detected, and SHALL NOT return an
absent result. A valid instance is reported as a result with no failures.

#### Scenario: The instance is valid

- **WHEN** a validator finds nothing wrong with the instance
- **THEN** it returns the shared success result, whose failure list is empty and which reports
  itself as valid

#### Scenario: The instance is invalid

- **WHEN** a validator rejects one or more properties
- **THEN** it returns a result carrying one failure per rejected property, and the result reports
  itself as not valid

### Requirement: Only error-severity failures block the request

A failure SHALL block the request only when its severity is error. Warning-severity and
info-severity failures SHALL be passed through to the handler and recorded for the operator.
Error is the default severity of a failure that does not state one.

#### Scenario: An error-severity failure is produced

- **WHEN** any validator for the request produces at least one error-severity failure
- **THEN** the request is rejected before the handler runs
- **AND** the handler is never invoked

#### Scenario: Only warning and info failures are produced

- **WHEN** every failure produced for the request is of warning or info severity
- **THEN** the handler runs normally and returns its result
- **AND** the non-blocking failures are recorded for the operator, with the count and the request
  type, at warning level

#### Scenario: Severities are mixed

- **WHEN** validators produce both an error-severity failure and a warning-severity failure for
  the same request
- **THEN** the request is rejected
- **AND** the rejection carries only the error-severity failures — a non-blocking failure never
  appears in what the caller is told blocked the request

### Requirement: Failures from all validators are aggregated

Where several validators are registered for one request type, the framework SHALL run all of them
and report their failures together, rather than stopping at the first validator that fails.

#### Scenario: Two validators each reject a different property

- **WHEN** two validators are registered for the request and each produces one failure
- **THEN** the request is rejected once, and the rejection carries both failures

### Requirement: Validation runs for both request shapes

Validation SHALL apply both to requests that produce a result and to requests that produce none.

#### Scenario: A request with no result fails validation

- **WHEN** a request that returns no result produces an error-severity failure
- **THEN** the request is rejected and the handler is never invoked

#### Scenario: A request with a result passes validation

- **WHEN** a request that returns a result produces no failures
- **THEN** the handler runs and its result is returned to the caller unchanged

### Requirement: Absence of validators is not a failure

A request type for which no validator is registered SHALL reach its handler unchanged. Validation
is opt-in per request type, not a gate every request must pass.

#### Scenario: No validator is registered for the request type

- **WHEN** a request is dispatched and no validator is registered for its type
- **THEN** the handler runs and returns its result

### Requirement: Validators are discoverable by assembly

The framework SHALL offer discovery that registers every concrete validator in a nominated
assembly against each request type it validates, so that adding a validator does not require
editing composition code.

#### Scenario: An assembly containing a validator is scanned

- **WHEN** a consumer nominates an assembly for validator discovery
- **THEN** every concrete class in that assembly implementing the validator contract is registered
  against each request type it validates, with a lifetime scoped to the request

### Requirement: Validation position in the pipeline is determined by registration order

Validation SHALL run as a pipeline stage whose position is determined by when it is registered
relative to other stages: the first stage registered is the outermost, and therefore runs first.
Registering validation first makes it reject invalid requests before authorization, auditing or
the handler execute.

#### Scenario: Validation is registered before other pipeline stages

- **WHEN** validation is registered before any other pipeline stage and a request fails validation
- **THEN** the request is rejected without the later stages or the handler running

#### Scenario: A consumer needs a different order

- **WHEN** a consumer registers another pipeline stage before validation
- **THEN** that stage runs first and validation runs inside it — the order is the consumer's to
  choose and the framework does not override it
