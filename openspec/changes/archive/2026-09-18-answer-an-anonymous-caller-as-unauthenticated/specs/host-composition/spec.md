# host-composition

## MODIFIED Requirements

### Requirement: Framework failures can be answered as a standard HTTP problem response

A host SHALL be able to opt into mapping the framework's own failure types to a standard
machine-readable problem response, so that a caller receives the same shape for every framework
rejection rather than one shape per failure type.

Mapping SHALL be opt-in, and SHALL leave any failure the framework did not raise untouched — a host
with its own error model must be able to keep it. A request that fails because it carries no identity —
an unauthenticated caller reaching an operation that needs a session — SHALL be answered as
unauthenticated, through the host's authentication challenge where it has one, and never as a server
error; the same failure for a caller that is authenticated means the host did not establish the
session context and SHALL NOT be converted, so that the misconfiguration stays loud.

#### Scenario: A request fails validation

- **WHEN** a request is rejected by validation
- **THEN** the response carries a client-error status and the failures, grouped so a caller can
  attribute each to the field it concerns

#### Scenario: A request is refused by authorization or tenant isolation

- **WHEN** an authenticated caller's request is refused for a missing role, a missing permission or a
  tenant-access denial
- **THEN** the response carries a forbidden status in the same problem shape

#### Scenario: A failure the framework did not raise

- **WHEN** any other failure reaches the boundary
- **THEN** it is not converted, and propagates unchanged

#### Scenario: The host does not opt in

- **WHEN** a host does not register the mapping
- **THEN** the framework converts nothing, and the host's own error handling applies

#### Scenario: An unauthenticated caller reaches an operation that needs a session

- **WHEN** a caller that is not authenticated sends a request that saves events or dispatches a command,
  and no guard refused it first
- **THEN** the response carries the unauthenticated status — through the host's challenge where an
  authentication scheme is registered, in the problem shape otherwise — and not a server error

#### Scenario: An authenticated caller has no session context

- **WHEN** an authenticated caller's request fails because no session context was established
- **THEN** the failure is not converted, and propagates unchanged
