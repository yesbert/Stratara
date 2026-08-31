## MODIFIED Requirements

### Requirement: In-memory doubles honour the contracts they stand in for

The framework SHALL offer in-memory implementations of the key store, the message bus, the session
provider, the membership store, the settings store, the API-key store and the blob encryptor, and
each SHALL honour the guarantees its contract states rather than being a stub.

A double that quietly ignores a guarantee turns every test that relies on it into a false pass.

#### Scenario: The key store double is used

- **WHEN** a test rotates, revokes or erases through the in-memory key store
- **THEN** rotation keeps earlier versions resolvable, revocation shreds one version, erasure shreds
  all of them, distinct scopes get distinct keys, and a returned key buffer is a copy

#### Scenario: The membership store double is used

- **WHEN** a test uses the in-memory membership store
- **THEN** forward and reverse lookups agree, writing is an upsert, active-tenant selection is
  membership-guarded, and the erasure sweeps clear selections along with memberships

#### Scenario: The API-key store double is used

- **WHEN** a test issues, validates, imports or revokes through the in-memory API-key store
- **THEN** expiry, revocation, the canonical-format check, the never-mutating import and the
  membership materialisation all behave as the real store does

#### Scenario: The settings store double is used

- **WHEN** a test writes and reads through the in-memory settings store
- **THEN** storage is per exact scope, writing an absent value deletes, and the erasure sweeps
  clear by dimension

#### Scenario: The message bus double is used

- **WHEN** a test publishes through the in-memory bus
- **THEN** a matching subscriber receives it, a subscriber for a different message type on the same
  topic does not, and every message is recorded for assertion whether or not anything was listening

#### Scenario: The message bus double stands in for a subscription established early

- **WHEN** a test establishes a subscription, publishes, and only then attaches a handler
- **THEN** the handler receives what was published in between, so a test cannot pass on start-up
  ordering the real broker would fail

#### Scenario: The blob encryptor double is used

- **WHEN** a test encrypts and decrypts through the test encryptor
- **THEN** the round trip recovers the plaintext and decrypting under a different scope fails, so a
  test cannot pass on scope binding the real encryptor would refuse
