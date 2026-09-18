# tenant-isolation

## MODIFIED Requirements

### Requirement: A rejected request reaches the caller as a forbidden response

A tenant-isolation rejection SHALL be signalled as a distinct failure type that a consumer's
boundary can map to an HTTP 403, separately from an authentication failure and from a validation
failure. The framework's own boundary mapping SHALL answer the rejection of a caller that is not
authenticated as an authentication failure instead.

#### Scenario: A consumer maps the rejection at its boundary

- **WHEN** a tenant-scoped request is rejected by the guard
- **THEN** the failure identifies itself as a tenant-access denial, carries the requested tenant and
  the session tenant, and can be caught without reference to the mediator package
