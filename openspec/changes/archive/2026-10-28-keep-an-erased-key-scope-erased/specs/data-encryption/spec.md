## ADDED Requirements

### Requirement: An erased subject is given no new key

Once a subject has been marked erased, the key store SHALL refuse to create key material for it.
For a tenant, that is a key of any level that names the tenant; for a user, a user-level key that
names the user — the same keys a subject's erasure shreds. Both the request for a scope's current key
and the rotation of a scope SHALL be refused for such a scope, SHALL create nothing, and SHALL fail
with a dedicated, documented exception that carries the refused scope, so a consumer can tell this
refusal from any other failure and answer it as data that is gone.

The mark SHALL hold for a scope that held a key before the erasure and for one that never did. A key
created after the erasure would make the data written under it readable for good, and no later
erasure would know it is there.

The mark SHALL be permanent; the framework SHALL offer no way to lift it. Revoking a single version
SHALL NOT mark anything, and marking a subject that is already marked SHALL succeed and change
nothing.

A key store that cannot mark a subject SHALL say so with `NotSupportedException`, the default for a
key store written before the mark existed. The framework's file-backed key store and its test key
store SHALL be able to; the development key store SHALL refuse it, as it refuses every erasure
operation.

#### Scenario: An encryption reaches an erased tenant

- **WHEN** a tenant has been marked erased and a value is then encrypted at any level under a scope
  that names the tenant
- **THEN** the encryption fails with the dedicated exception naming the scope, and no key for that
  scope exists afterwards

#### Scenario: A combination that never had a key names an erased tenant

- **WHEN** a tenant has been marked erased and a key is requested for a scope that names the tenant
  together with a user who never wrote in it
- **THEN** the request is refused rather than creating a first version

#### Scenario: An erased scope is rotated

- **WHEN** a scope that names an erased subject is rotated
- **THEN** the rotation is refused and no version is created

#### Scenario: An erased user's tenant is untouched

- **WHEN** a user has been marked erased
- **THEN** user-level keys naming the user are refused, and keys of the tenants the user belonged to
  that do not name the user are still created and returned

#### Scenario: Another subject is unaffected

- **WHEN** one subject has been marked erased
- **THEN** keys for scopes that name neither that subject nor another erased one are created,
  returned and rotated as before

#### Scenario: The last version is revoked

- **WHEN** every version of a scope has been revoked and the scope's current key is requested
- **THEN** a new version is created — revocation is not an erasure

#### Scenario: The store is reopened

- **WHEN** a subject has been marked erased in the file-backed key store and the store is opened again
  from its file
- **THEN** the subject is still refused

## MODIFIED Requirements

### Requirement: One key store serves several processes over shared storage

Where several processes share one key store file, a process SHALL see keys created by another
process, and concurrent creation for the same scope SHALL converge on one key rather than
overwriting each other. A subject one process marks erased SHALL be refused by every other process
sharing the file, including one that was already running.

Processes sharing one file SHALL run the same file format. A key store SHALL refuse to open a file
whose format is newer than the one it knows, rather than read it partially and write it back without
what it did not understand. A process on a release from before a format change cannot know to refuse,
so the framework SHALL state, for each release that changes the format, that every process sharing a
file is upgraded together.

#### Scenario: One process creates a key another needs

- **WHEN** one process creates a key for a new scope and a second process, already running, asks
  for that scope's key
- **THEN** the second process resolves it

#### Scenario: Two processes create a key for the same scope at once

- **WHEN** two processes request a key for the same previously unknown scope concurrently
- **THEN** both end up with the same key rather than one silently replacing the other's

#### Scenario: Two processes create keys for different scopes at once

- **WHEN** two processes create keys for different scopes concurrently
- **THEN** both keys survive

#### Scenario: One process marks a subject another is about to write for

- **WHEN** one process marks a subject erased and a second process, already running, then requests a
  key for a scope that names the subject
- **THEN** the second process refuses the request

#### Scenario: A file written by a newer release is opened

- **WHEN** a key store opens a file whose format is newer than the one it knows
- **THEN** it fails to open, naming the format it found and the one it supports, and leaves the file
  unchanged
