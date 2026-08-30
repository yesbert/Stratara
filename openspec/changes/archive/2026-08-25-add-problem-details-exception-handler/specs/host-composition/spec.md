## ADDED Requirements

### Requirement: Framework failures can be answered as a standard HTTP problem response

A host SHALL be able to opt into mapping the framework's own failure types to a standard
machine-readable problem response, so that a caller receives the same shape for every framework
rejection rather than one shape per failure type.

Mapping SHALL be opt-in, and SHALL leave any failure the framework did not raise untouched — a host
with its own error model must be able to keep it.

#### Scenario: A request fails validation

- **WHEN** a request is rejected by validation
- **THEN** the response carries a client-error status and the failures, grouped so a caller can
  attribute each to the field it concerns

#### Scenario: A request is refused by authorization or tenant isolation

- **WHEN** a request is refused for a missing role, a missing permission or a tenant-access denial
- **THEN** the response carries a forbidden status in the same problem shape

#### Scenario: A failure the framework did not raise

- **WHEN** any other failure reaches the boundary
- **THEN** it is not converted, and propagates unchanged

#### Scenario: The host does not opt in

- **WHEN** a host does not register the mapping
- **THEN** the framework converts nothing, and the host's own error handling applies
