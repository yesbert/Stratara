## MODIFIED Requirements

### Requirement: A denial reaches an HTTP caller as 403

The framework SHALL offer one opt-in boundary mapping that turns an authorization denial — role,
permission or tenant-access — into an HTTP 403 response carrying the same machine-readable problem
body as every other framework rejection, and SHALL let every other failure through unchanged.

A host that does not opt in SHALL see the denial propagate to its own error handling untouched, so a
host with an error model of its own keeps it. There is no second mapping that answers a denial with a
status code and no body: a host that opts in gets the problem shape, and a host that does not gets
nothing converted.

#### Scenario: An authorization denial reaches the boundary

- **WHEN** an authorization or tenant-access denial propagates to the boundary of a host that opted
  into the mapping
- **THEN** the response status is 403 and the body is the framework's standard problem shape

#### Scenario: An unrelated failure reaches the boundary

- **WHEN** any other failure propagates to the boundary
- **THEN** it is not converted, and propagates unchanged

#### Scenario: Nothing fails

- **WHEN** the request completes without a failure
- **THEN** the boundary leaves the response untouched

#### Scenario: The host does not opt in

- **WHEN** an authorization or tenant-access denial reaches the boundary of a host that did not
  register the mapping
- **THEN** the denial is not converted, and the host's own error handling sees it
