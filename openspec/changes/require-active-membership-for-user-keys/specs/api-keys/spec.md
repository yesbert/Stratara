## MODIFIED Requirements

### Requirement: Validation is fail-closed and enforces expiry and revocation

Validating a key SHALL succeed only for a key that exists, has not been revoked and has not expired,
and, for a key bound to a user, whose user holds an active membership in the key's tenant. Anything
else SHALL fail without distinguishing why.

The membership is a condition of validating, not a change to the key: a failure for this reason SHALL
NOT revoke or alter the key, and the key SHALL validate again once the membership is active again.
Every implementation of `IApiKeyStore` SHALL apply this condition.

A key that fails this way yields no principal at all, so there is no tenant for the request to keep.

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

#### Scenario: The bound user's membership is active

- **WHEN** a key bound to a user is presented and the user holds an active membership in the key's
  tenant
- **THEN** validation succeeds and returns the key's descriptor

#### Scenario: The bound user's membership is former or pending

- **WHEN** a key bound to a user is presented and the user's membership in the key's tenant is former
  or pending
- **THEN** validation fails

#### Scenario: The bound user has no membership in the key's tenant

- **WHEN** a key bound to a user is presented and the user's membership in the key's tenant was removed,
  whatever memberships the user holds in other tenants
- **THEN** validation fails

#### Scenario: The membership becomes active again

- **WHEN** a key bound to a user failed validation because of the membership, and the user then holds
  an active membership in the key's tenant again
- **THEN** the same key validates, provided it has not been revoked and has not expired

#### Scenario: A machine key is presented

- **WHEN** a key with no bound user is presented
- **THEN** validation does not depend on any user's membership

#### Scenario: A user-bound key arrives over HTTP without an active membership

- **WHEN** a request carries, in the configured header, a key bound to a user who holds no active
  membership in the key's tenant
- **THEN** authentication fails and the request carries no principal and no tenant claim, including on
  an endpoint that requires only authentication
