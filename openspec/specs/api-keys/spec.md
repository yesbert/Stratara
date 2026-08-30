# api-keys Specification

## Purpose
Let a machine, or a user's own long-lived token, authenticate without a password — and without
creating a second set of rules about what that identity may do.

## Requirements

### Requirement: A key is high-entropy, prefixed and canonically shaped

An issued key SHALL consist of a fixed prefix followed by 32 cryptographically random bytes in
URL-safe base64 encoding. Only that exact shape SHALL be accepted anywhere a raw key is supplied.

The prefix makes a leaked key recognisable in a log or a repository scan; the fixed shape is what
makes validation possible at all, because entropy cannot be checked but form can.

#### Scenario: A key is created

- **WHEN** a key is created
- **THEN** it carries the prefix, encodes the full 32 random bytes, and differs from every other
  key created

#### Scenario: A malformed value is presented

- **WHEN** a value of the wrong length, without the prefix, or containing a character outside the
  URL-safe alphabet is presented
- **THEN** it is rejected as malformed

### Requirement: Only a digest of a key is stored

The raw key SHALL be shown to the issuer once and never stored. Storage SHALL hold only a digest,
indexed uniquely so that validation is a single lookup.

Salting is deliberately absent: the key is 256 bits of randomness, so a precomputation attack has
nothing to precompute against, and a per-row salt would make the lookup a scan.

#### Scenario: A key is issued

- **WHEN** a key is issued
- **THEN** the raw value is returned to the caller once, and only its digest is persisted

#### Scenario: A key is presented for validation

- **WHEN** a key is presented
- **THEN** it is resolved by digest in a single lookup

### Requirement: A machine key becomes a membership, not a parallel role list

Issuing a key for a machine SHALL materialise a membership in the target tenant whose member
identity is the key itself and whose roles are the key's roles. Every authorization decision about
that key SHALL then go through the ordinary membership path.

There is no second evaluator: a machine actor resolves roles, permissions and cross-tenant access
through exactly the code a human actor does.

#### Scenario: A machine key is issued

- **WHEN** a key is issued with roles and no bound user
- **THEN** a membership is created in the target tenant carrying those roles, identified by the key

#### Scenario: A machine key is used

- **WHEN** a request authenticates with a machine key
- **THEN** its roles and permissions resolve through the membership directory, not through the key
  record

### Requirement: A key bound to a user acts as that user and carries no roles of its own

A key may be bound to a user, in which case it SHALL act as that user and SHALL NOT carry roles.
Issuing one SHALL require the user to hold an active membership in the target tenant.

A user's own token must not be able to exceed the user: giving it roles would make it a privilege
escalation with a longer lifetime than a session.

#### Scenario: A user-bound key is issued

- **WHEN** a key is issued bound to a user who holds an active membership in the tenant
- **THEN** it is created carrying no roles of its own

#### Scenario: The user has no active membership

- **WHEN** a key is issued bound to a user with no active membership in the target tenant
- **THEN** issuance is refused

#### Scenario: A user-bound key is used

- **WHEN** a request authenticates with a user-bound key
- **THEN** it acts as that user, with exactly the user's roles and permissions

### Requirement: Validation is fail-closed and enforces expiry and revocation

Validating a key SHALL succeed only for a key that exists, has not been revoked and has not expired.
Anything else SHALL fail without distinguishing why.

#### Scenario: A valid key is presented

- **WHEN** a live, unexpired, unrevoked key is presented
- **THEN** validation succeeds and returns the key's descriptor

#### Scenario: An unknown or garbage value is presented

- **WHEN** a value that is not a stored key is presented
- **THEN** validation fails

#### Scenario: An expired key is presented

- **WHEN** a key past its expiry is presented
- **THEN** validation fails — expiry is enforced when the key is used, not only when it is issued

#### Scenario: A revoked key is presented

- **WHEN** a revoked key is presented
- **THEN** validation fails

### Requirement: Revocation removes the identity as well as the key

Revoking a key SHALL stop it validating and SHALL remove the membership it materialised, so no
authorization path outlives the key.

#### Scenario: A machine key is revoked

- **WHEN** a machine key is revoked
- **THEN** it no longer validates and its membership is gone

### Requirement: Erasure sweeps cover the key plane

Removing a tenant SHALL remove its keys and the memberships they materialised. Removing a user SHALL
remove the keys bound to that user, and only those.

#### Scenario: A tenant is swept

- **WHEN** a tenant's keys are swept
- **THEN** its keys and their machine memberships are removed

#### Scenario: A user is swept

- **WHEN** a user's keys are swept
- **THEN** only the keys bound to that user are removed, and the tenant's machine keys are untouched

### Requirement: A key can be imported with a value the caller already knows

The framework SHALL support creating a machine key from a raw value supplied by the caller, so a
key can exist before anything boots — for container orchestration, provisioning pipelines,
self-hosting and end-to-end test hosts.

Import SHALL accept only the canonical shape, and SHALL NOT accept a bound user, so a personal
access token cannot be imported.

#### Scenario: A key is imported

- **WHEN** a well-formed raw value is imported for a tenant with a name and roles
- **THEN** the key is created, validates, and materialises its membership like an issued key

#### Scenario: A value outside the canonical shape is imported

- **WHEN** the supplied value does not have the canonical form
- **THEN** import is refused

#### Scenario: A personal access token is imported

- **WHEN** an import is attempted for a key bound to a user
- **THEN** it is structurally impossible — the import request carries no user, so the path cannot
  produce one

### Requirement: Import is idempotent and never mutates an existing key

Repeating an import SHALL return the existing key's descriptor unchanged. Where the repeated request
differs in name, roles or expiry, the difference SHALL be recorded and ignored rather than applied.

Configuration drift must not be able to escalate a key's roles: a compromised or mistaken
configuration file that re-imports a known key with administrator roles changes nothing.

#### Scenario: The same key is imported twice identically

- **WHEN** an import is repeated with the same value and the same attributes
- **THEN** the existing descriptor is returned and nothing is written

#### Scenario: The same key is imported with different attributes

- **WHEN** an import is repeated with a different name, different roles or a different expiry
- **THEN** the stored key is unchanged, the divergence is recorded, and the existing descriptor is
  returned

#### Scenario: The key's membership went missing

- **WHEN** an import is repeated for a key whose materialised membership no longer exists
- **THEN** the membership is restored — this is the one thing a repeat import does write

### Requirement: Import refuses a key that is not the caller's to claim

Importing a value that already exists as a key of another tenant, as a user-bound token, as a
revoked key, or as an expired key SHALL fail.

#### Scenario: The value belongs to another tenant

- **WHEN** the imported value is already a key of a different tenant
- **THEN** import fails, and the key is not rebound

#### Scenario: The value is a user-bound token

- **WHEN** the imported value is already stored as a key bound to a user
- **THEN** import fails

#### Scenario: The value was revoked or has expired

- **WHEN** the imported value is a revoked or expired key
- **THEN** import fails — an import cannot reinstate a revoked key or extend an expired one

### Requirement: A key authenticates over HTTP as a first-class scheme

The framework SHALL offer an authentication scheme that reads a key from a request header, and
optionally from the query string when a host opts in. A successful authentication SHALL produce a
principal carrying the key's identity and its tenant claim, so the rest of the pipeline is unchanged.

Query-string keys are off by default because a query string is logged by proxies, servers and
browsers alike.

#### Scenario: A valid key arrives in the header

- **WHEN** a request carries a valid key in the configured header
- **THEN** it is authenticated, and the principal carries the key's identity and tenant claim

#### Scenario: An invalid key arrives

- **WHEN** a request carries a value that does not validate
- **THEN** authentication fails

#### Scenario: No key is present

- **WHEN** a request carries no key at all
- **THEN** the scheme produces no result, so another configured scheme may still authenticate the
  request

#### Scenario: A key arrives in the query string

- **WHEN** a request carries a key in the query string and the host has not opted in
- **THEN** it is not accepted

#### Scenario: The header name is customised

- **WHEN** a host configures a different header name
- **THEN** that name is read instead

### Requirement: A host can accept several credential kinds on one endpoint

The framework SHALL offer a scheme selector that routes a request to the key scheme, a bearer scheme
or a cookie scheme based on what the request carries, with the schemes configurable.

#### Scenario: A request carries a key header

- **WHEN** a request carries the key header
- **THEN** it is routed to the key scheme

#### Scenario: A request carries a bearer token

- **WHEN** a request carries a bearer authorization header
- **THEN** it is routed to the bearer scheme

#### Scenario: A request carries neither

- **WHEN** a request carries neither
- **THEN** it is routed to the cookie scheme
