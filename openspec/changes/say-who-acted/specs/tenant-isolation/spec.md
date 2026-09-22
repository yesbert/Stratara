## ADDED Requirements

### Requirement: Work the platform starts is not a cross-tenant operation

A session whose actor identities are the reserved system values names work the platform started on
a tenant's behalf — a timer, a saga step, a sweep — rather than one tenant acting on another. The
guard SHALL recognise it as such and SHALL NOT refer it to the cross-tenant authorizer, so that a
host enabling strict isolation does not have to teach its authorizer about the framework's own
background work. The subject check SHALL apply unchanged: such a session still operates on exactly
one tenant, and a request targeting a different one is still refused.

Permitted platform-initiated work SHALL be recorded distinguishably from a permitted cross-tenant
operation, so that an audit reading can tell "the platform acted for this tenant" from "another
tenant acted on it".

A host SHALL be able to require that platform-initiated work be referred to the cross-tenant
authorizer like any other cross-tenant operation, for a deployment that wants to decide for itself.

#### Scenario: Platform-initiated work under strict mode

- **WHEN** the mode is strict, the session's actor identities are the reserved system values, and
  the request targets the session's data-owner tenant
- **THEN** the request proceeds, the cross-tenant authorizer is not consulted, and the work is
  recorded as platform-initiated with the tenant it was done for

#### Scenario: Platform-initiated work targeting another tenant

- **WHEN** such a session is used for a request that targets a tenant other than the session's
  data owner
- **THEN** the request is refused by the subject check, exactly as any other session would be

#### Scenario: A host refers platform-initiated work to its authorizer

- **WHEN** a host configures platform-initiated work to be authorized, and such a request is made
- **THEN** the cross-tenant authorizer decides, and a refusal is a tenant-access-denied failure

#### Scenario: A host configures nothing

- **WHEN** a host that has not configured the framework's tenant isolation dispatches a request
  whose session carries ordinary actor identities
- **THEN** nothing about its behaviour differs from before
