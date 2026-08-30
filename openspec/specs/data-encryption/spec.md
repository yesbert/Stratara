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
overwriting each other.

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
