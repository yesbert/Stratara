## ADDED Requirements

### Requirement: A scope that claims isolation but collapses to one key is refused

When data is encrypted at a sensitivity level that claims isolation, at least one identifying
dimension SHALL be present. If none is, the framework SHALL refuse to encrypt rather than fall back to
a scope shared by every subject in the system.

Falling back is not a weaker version of the guarantee, it is the absence of it: every such value
resolves to one scope and therefore one key, so destroying that key to erase one subject destroys all
of them. The level continues to claim isolation the data does not have, and nothing observable
distinguishes the two.

A level that claims isolation by a narrower dimension than the one present SHALL NOT be refused. A
value scoped by user but carrying only a tenant still separates tenants from one another; the
isolation is coarser than the level's name suggests, but it exists, and an erasure at that scope
reaches one tenant rather than everyone.

A consumer whose data genuinely has no identifying dimension SHALL express that with the level that
claims none, which states a single system-wide key and no isolation.

The refusal SHALL apply where data is written. Data already encrypted into such a scope SHALL remain
decryptable, because refusing to read it would destroy access to it rather than protect it.

Outside development the refusal SHALL be an error. In development it SHALL be reported without
stopping the operation, so that the mistake is visible while it is being made.

#### Scenario: A tenant-scoped value is encrypted with no tenant

- **WHEN** a value at the tenant level is encrypted outside development and no tenant is present
- **THEN** the operation fails, and the failure names the level, what was missing, and the level to
  use for data that has no identifying dimension

#### Scenario: A user-scoped value is encrypted with neither user nor tenant

- **WHEN** a value at the user level is encrypted outside development with no user and no tenant
- **THEN** the operation fails in the same way, because nothing remains to isolate by

#### Scenario: A user-scoped value is encrypted with a tenant but no user

- **WHEN** a value at the user level is encrypted with a tenant present and no user
- **THEN** it succeeds — the scope still separates one tenant's data from another's, which is
  coarser than the level names but is isolation rather than its absence

#### Scenario: The same mistake is made in development

- **WHEN** an encryption that would be refused is attempted in development
- **THEN** it is reported and allowed to proceed, so local work continues and the mistake is still
  visible

#### Scenario: Data already written into such a scope is read

- **WHEN** a value encrypted into such a scope before the refusal existed is decrypted
- **THEN** it decrypts normally — the refusal governs writing, never reading

#### Scenario: Data with no identifying dimension is encrypted at the level that claims none

- **WHEN** a value is encrypted at the level that claims no isolation
- **THEN** the refusal does not apply, because a single system-wide key is what that level states
