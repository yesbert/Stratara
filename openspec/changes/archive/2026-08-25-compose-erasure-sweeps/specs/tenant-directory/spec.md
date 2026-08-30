## ADDED Requirements

### Requirement: A subject's erasure can be performed as one operation

The framework SHALL offer a single operation that erases a subject across every plane it holds data
in — membership, key material, settings and API keys — in an order that leaves nothing unreachable
before it has been removed.

The operation SHALL report what it covered, and the framework SHALL state what it does not cover, so
a consumer can tell what remains their own responsibility.

#### Scenario: A user is erased

- **WHEN** a user's erasure is performed
- **THEN** their memberships, their active-tenant selection, their settings across all tenants, the
  API keys bound to them and their key material are all removed, and data encrypted under their keys
  becomes unrecoverable

#### Scenario: A tenant is erased

- **WHEN** a tenant's erasure is performed
- **THEN** its memberships, its machine keys and their materialised memberships, its settings across
  all users and its key material are all removed

#### Scenario: A consumer asks what erasure covers

- **WHEN** a consumer needs to know whether an erasure is complete
- **THEN** the framework states which planes it covers and which it does not — in particular that
  read models a consumer's own projections built, and any unprotected data in the event stream, are
  the consumer's to handle

#### Scenario: A plane's sweep fails partway

- **WHEN** one plane's sweep fails during a composed erasure
- **THEN** the failure identifies which plane it was, so the operation can be resumed rather than
  restarted blindly
