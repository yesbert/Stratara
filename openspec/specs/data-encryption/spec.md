# data-encryption Specification

## Purpose
Protect data at rest with keys scoped narrowly enough that destroying one key destroys exactly one
subject's data and nothing else — so that an erasure request can be answered on an append-only
store, where deleting the data itself is not an option.

## Requirements

### Requirement: Keys are scoped, and a scope is the unit of erasure

The framework SHALL manage data-encryption keys per scope, where a scope combines a sensitivity
level with the tenant and user the data belongs to. Distinct scopes SHALL receive distinct keys.

Destroying a scope's keys is what makes that scope's data unrecoverable; a key shared across scopes
would make erasure for one subject destroy another subject's data.

#### Scenario: Two different scopes request a key

- **WHEN** keys are requested for two scopes that differ in tenant, in user, or in sensitivity level
- **THEN** the keys returned are different

#### Scenario: The same scope requests a key twice

- **WHEN** the current key for a scope is requested more than once
- **THEN** the same key and key identity are returned — a scope has one current key, not one per call

### Requirement: A key has versions, and rotation does not break existing ciphertext

Rotating a scope SHALL create a new current version while leaving earlier versions resolvable, so
that data encrypted before the rotation remains readable.

#### Scenario: A scope is rotated

- **WHEN** a scope is rotated
- **THEN** subsequent encryptions use the new version
- **AND** data encrypted under the previous version can still be decrypted

### Requirement: Revoking a version destroys exactly that version

Revoking a key version SHALL make that version permanently unresolvable while leaving the scope
usable: the scope's current key rolls forward to a new version rather than the scope becoming dead.

#### Scenario: A version is revoked

- **WHEN** a key version is revoked
- **THEN** that version can no longer be resolved and data encrypted under it cannot be recovered
- **AND** the scope's current key is a new version, so new writes still succeed

### Requirement: Erasing a scope destroys every version of it

Erasing a scope SHALL destroy every version of that scope's keys, making all data ever encrypted
under that scope unrecoverable. This is the mechanism by which an erasure obligation is satisfied
without mutating the stored data.

#### Scenario: A scope is erased

- **WHEN** a scope is erased
- **THEN** no version of that scope's key can be resolved afterwards, including versions superseded
  by earlier rotations

### Requirement: Keys are never stored in the clear

The production key store SHALL persist data-encryption keys only in wrapped form, under a master
key supplied separately from the store file. Possession of the store file alone SHALL NOT be
sufficient to recover any data-encryption key.

#### Scenario: The store file is inspected

- **WHEN** the persisted key store file is read directly
- **THEN** it contains only wrapped key material — no data-encryption key appears in the clear

#### Scenario: The store is opened with the wrong master key

- **WHEN** the store is opened under a master key other than the one that wrapped its contents
- **THEN** no data-encryption key can be unwrapped

### Requirement: The master key is validated at configuration time

The framework SHALL reject a master key that is absent, not valid base64, or not of the length
required for AES-256, at the point it is configured rather than at first use.

#### Scenario: The master key is missing

- **WHEN** no master key is configured
- **THEN** configuration fails

#### Scenario: The master key is malformed or the wrong length

- **WHEN** the configured master key is not valid base64, is shorter than AES-256 requires, or is
  longer
- **THEN** configuration fails rather than silently truncating or padding

### Requirement: Encryption is authenticated and bound to its context

Encrypted data SHALL be protected by an authentication tag covering both the ciphertext and the
context it was encrypted for. Any alteration of the ciphertext or the tag, decryption under a
different key, and decryption under a different context SHALL all fail rather than returning
plausible plaintext.

#### Scenario: The ciphertext is altered

- **WHEN** stored ciphertext is modified and then decrypted
- **THEN** decryption fails

#### Scenario: The authentication tag is altered

- **WHEN** the authentication tag is modified and the ciphertext is decrypted
- **THEN** decryption fails

#### Scenario: The wrong key is used

- **WHEN** ciphertext is decrypted with a key other than the one it was encrypted under
- **THEN** decryption fails

#### Scenario: The context differs

- **WHEN** ciphertext is decrypted with a different context than it was encrypted with
- **THEN** decryption fails

#### Scenario: Each encryption is unique

- **WHEN** the same plaintext is encrypted twice under the same key
- **THEN** the two results differ, because each encryption uses a fresh nonce

### Requirement: Blob ciphertext is bound to the scope and purpose it was written for

Encrypting a stream SHALL bind the result to both the scope and a caller-supplied purpose, so that
ciphertext written for one purpose in one scope cannot be decrypted as another.

#### Scenario: A blob round-trips within its own scope

- **WHEN** a stream is encrypted for a scope and purpose and decrypted for the same scope
- **THEN** the original bytes are recovered

#### Scenario: A blob is read under a different scope

- **WHEN** an encrypted stream is decrypted under a different scope than it was written for
- **THEN** decryption fails

#### Scenario: A blob written by an earlier format version is read

- **WHEN** a stream encrypted under a previously shipped stream format is decrypted
- **THEN** it is still readable — a format revision does not strand data already written

### Requirement: Fields are encrypted selectively and transparently

A consumer SHALL be able to mark a whole type, an individual property or a primary-constructor
parameter for encryption, and the framework SHALL encrypt only what is marked while serializing
everything else normally.

#### Scenario: Only some properties are marked

- **WHEN** a type has both marked and unmarked properties
- **THEN** the serialized form carries ciphertext for the marked properties and ordinary values for
  the rest

#### Scenario: Nothing is marked

- **WHEN** a type carries no encryption marking
- **THEN** it serializes as ordinary data with no key lookup performed

#### Scenario: A marked property holds no value

- **WHEN** a marked property is absent
- **THEN** the serialized form records its absence rather than encrypting a placeholder

### Requirement: Unreadable encrypted fields degrade rather than fail

Where a field's key has been destroyed, deserialization SHALL yield an absent value for that field
rather than failing the whole object, so that erasing a subject's key does not make every record
mentioning that subject unreadable.

#### Scenario: A field's key has been revoked

- **WHEN** an object containing an encrypted field is deserialized after that field's key was
  revoked
- **THEN** the field reads as absent and the object's other fields are recovered normally

#### Scenario: Data was written before the type was marked for encryption

- **WHEN** ordinary serialized data is deserialized as a type that is now marked for encryption
- **THEN** it is read as ordinary data rather than rejected

### Requirement: A metadata mismatch is refused at start-up

The framework SHALL verify at start-up that its own decision about whether a registered type
requires encryption agrees with that type's actual markings, and SHALL refuse to start on a
mismatch.

A type the framework believes is unencrypted, but which a consumer has marked, would be written in
the clear silently — the exact failure that is invisible in testing and irreversible in production.

#### Scenario: The markings and the framework's decision agree

- **WHEN** every registered type's markings match the framework's encryption decision for it
- **THEN** start-up proceeds

#### Scenario: They disagree

- **WHEN** a registered type is marked for encryption but the framework's decision says otherwise,
  or the reverse
- **THEN** start-up fails, naming the type and both values

### Requirement: The development key store cannot run outside development

The framework SHALL ship a key store for local development that uses a fixed, publicly known key,
and it SHALL refuse to be constructed in any environment other than development. Where it is in
use at all, the host SHALL emit a warning at start-up.

The guard is a whitelist, not a blacklist: an environment named anything other than development —
staging, QA, UAT, preview, or a name the framework has never heard of — is refused.

The development store SHALL NOT simulate an operation it does not perform. In particular it SHALL
NOT report a successful erasure without shredding anything.

#### Scenario: The host runs in development

- **WHEN** the development key store is constructed in the development environment
- **THEN** it works, and the host emits a start-up warning that it is active

#### Scenario: The host runs anywhere else

- **WHEN** the development key store is constructed in any environment that is not development
- **THEN** construction fails, naming the current environment and how to register a real key store

#### Scenario: A real key store is registered

- **WHEN** a real key store is registered before the security composition
- **THEN** that store is used, the development fallback is never constructed, and no warning is
  emitted

#### Scenario: An erasure is attempted against the development store

- **WHEN** a revocation or a scope erasure is attempted against the development key store
- **THEN** it fails rather than reporting success — a development stub must not make an erasure path
  look exercised when nothing was shredded

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
