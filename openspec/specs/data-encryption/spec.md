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

Data that belongs to a user rather than to any of their tenants SHALL have a key of that user alone.
Data whose owner is the user alone — an event recorded for an owner that names the user and no
tenant — SHALL be encrypted under keys of that user alone, at whatever level a field is marked. Those
keys SHALL be shredded by the user's erasure and SHALL NOT be shredded by the erasure of any tenant
the user belongs to. The documentation
SHALL describe this as the way to write data that follows a person across tenants, and SHALL state
that the scope is fixed when the data is written: data written for one of the user's tenants stays
bound to that tenant and is shredded with it.

#### Scenario: Two different scopes request a key

- **WHEN** keys are requested for two scopes that differ in tenant, in user, or in sensitivity level
- **THEN** the keys returned are different

#### Scenario: The same scope requests a key twice

- **WHEN** the current key for a scope is requested more than once
- **THEN** the same key and key identity are returned — a scope has one current key, not one per call

#### Scenario: A user's own data is written

- **WHEN** an event with a field at the user level is recorded for an owner that names a user alone
- **THEN** the field is encrypted under a key that names the user and no tenant of theirs, and the
  event reads back normally under a session of any of the user's tenants

#### Scenario: A user's own data outlives one of their tenants

- **WHEN** a user's own data has been written and a tenant the user belongs to is then erased
- **THEN** the data still reads normally

#### Scenario: A user's own data is erased with the user

- **WHEN** a user's own data has been written, with fields marked at the user, the tenant and the
  confidential level, and the user is then erased
- **THEN** the data's encrypted fields read as absent at every level, and no key for them is created
  again

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

Ciphertext written in the current format SHALL be authenticated against the whole scope — its
sensitivity level, its tenant and its user — and the purpose. A stream in an earlier format SHALL keep
the binding that format was written with: the previous format binds the tenant and the purpose only,
and the format before it may record no purpose at all.

A reader MAY name the purpose it expects, through the `ISecureBlobEncryptor.DecryptAsync` form that
takes one; the framework's encryptor SHALL then fail authentication for a stream written for another
purpose. A reader that names none is given whatever the stream was written for. That form SHALL be a
default interface method, so an existing implementation keeps compiling and does not check the purpose
until it overrides it.

`StrataraBlobEncryptionOptions.WriteV2Blobs`, read from `Stratara:BlobEncryption:WriteV2Blobs` and
`false` by default, SHALL keep new streams in the previous format for a rolling upgrade. Both formats
SHALL be read whatever it is set to. The documentation SHALL state that a release before the current
format cannot read it, and that the option is for as long as such a host still reads what this one
writes.

#### Scenario: A blob round-trips within its own scope

- **WHEN** a stream is encrypted for a scope and purpose and decrypted for the same scope
- **THEN** the original bytes are recovered

#### Scenario: A blob is read under a different scope

- **WHEN** a stream encrypted in the current format is decrypted under a scope that differs in its
  level, its tenant or its user from the one it was written for
- **THEN** decryption fails

#### Scenario: A blob written by an earlier format version is read

- **WHEN** a stream encrypted under a previously shipped stream format is decrypted
- **THEN** it is still readable — a format revision does not strand data already written

#### Scenario: A blob is read for another purpose

- **WHEN** a stream that records its purpose is decrypted by a reader naming another purpose
- **THEN** decryption fails

#### Scenario: A reader names no purpose

- **WHEN** a stream is decrypted under its own scope by a reader that names no purpose
- **THEN** it decrypts, whatever purpose it was written for

#### Scenario: A blob records no purpose

- **WHEN** a stream in an earlier format that records no purpose is decrypted by a reader naming one
- **THEN** it decrypts — there is no purpose to check it against

#### Scenario: An encryptor does not override the purpose-taking form

- **WHEN** an implementation of `ISecureBlobEncryptor` written before that form existed is asked to
  decrypt for a purpose
- **THEN** it compiles and decrypts as it did, without checking the purpose

#### Scenario: A rolling upgrade writes the previous format

- **WHEN** `WriteV2Blobs` is `true` and a stream is encrypted
- **THEN** it is written in the previous format, and a host on the current release reads it

#### Scenario: A blob in the previous format is read under another scope of its tenant

- **WHEN** a stream in the previous format is decrypted under a scope of the same tenant that differs
  in its level or its user
- **THEN** it decrypts, because that format does not bind them

#### Scenario: A blob in the previous format is read under another tenant

- **WHEN** a stream in the previous format is decrypted under a scope of another tenant
- **THEN** decryption fails

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

Where a field's key has been destroyed — by revoking its version, by erasing its scope, or by erasing
the subject it names — deserialization SHALL yield an absent value for that field rather than failing
the whole object, so that erasing a subject's key does not make every record mentioning that subject
unreadable.

This SHALL hold wherever the framework reads its own stored data, including when it maps events read
from a stream and when it replays a stream into an aggregate or a projection: the event is delivered
with the field absent and its other fields intact, and the replay continues with the next event.

An absent field of a reference or nullable type SHALL read as null; one of a non-nullable value type
SHALL read as that type's default value. The documentation SHALL state that only a nullable field lets
a reader tell an erased value from a stored default, and recommend nullable types for fields that can
be erased.

#### Scenario: A field's key has been revoked

- **WHEN** an object containing an encrypted field is deserialized after that field's key was
  revoked
- **THEN** the field reads as absent and the object's other fields are recovered normally

#### Scenario: A field's subject has been erased

- **WHEN** an object containing a field encrypted for a user is deserialized after that user's
  erasure
- **THEN** the field reads as absent and the object's other fields are recovered normally

#### Scenario: A stream is replayed across an erasure

- **WHEN** a stream holding events whose encrypted fields name an erased subject is replayed into an
  aggregate or a projection
- **THEN** every event is delivered in order, those fields read as absent, the events' other fields
  read normally, and the replay completes

#### Scenario: An erased value-type field is read

- **WHEN** an encrypted field of a non-nullable value type is read after its key was destroyed
- **THEN** it reads as the type's default value, and the same field declared nullable reads as null

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

A key another process has revoked, or whose scope another process has erased, SHALL stop resolving at
a process's next lookup, although that process held the key in memory.

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

#### Scenario: One process revokes a key another holds in memory

- **WHEN** a process has resolved a key and another process sharing the file then revokes it
- **THEN** the first process's next lookup does not resolve it — verified on the file-backed key store
  with two instances over one file

#### Scenario: One process erases a scope another holds in memory

- **WHEN** a process has resolved a key and another process sharing the file then erases its scope
- **THEN** the first process's next lookup does not resolve it — verified on the file-backed key store
  with two instances over one file

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
names the user, and a key of any level that names the user and no tenant of theirs — the same keys a
subject's erasure shreds. Both the request for a scope's current key
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
### Requirement: What a stream stores is encrypted field by field

An event payload type or an aggregate type SHALL NOT be marked for encryption as a whole. Where a
registered event or aggregate type is so marked, the host SHALL refuse to start, naming the type and
stating that its properties are to be marked individually instead.

A type encrypted as a whole has no field left to recover once its subject is erased: the event cannot
be delivered and the snapshot cannot be restored, so a replay or a rebuild stops at exactly the point
the erasure reached, and keeps stopping there. Refusing at start-up turns that into a mistake seen on
the first run rather than an outage that begins on the first erasure.

An event payload type the start-up check cannot recognise as one — registered other than through an
aggregate — SHALL be refused when it is appended, before anything is written, with the same naming
and remedy.

Other registered types — a command, a message, a stored document — SHALL remain permitted to be
marked as a whole.

#### Scenario: An event type is marked as a whole

- **WHEN** a registered event payload type carries the encryption marking on the type itself
- **THEN** the host fails to start, naming the type and the remedy

#### Scenario: An aggregate type is marked as a whole

- **WHEN** a registered aggregate type carries the encryption marking on the type itself
- **THEN** the host fails to start, naming the type and the remedy

#### Scenario: An event type the start-up check did not see is appended

- **WHEN** an event whose payload type is marked as a whole, and which no registered aggregate
  applies, is appended to a stream
- **THEN** the append fails before anything is written, naming the type and the remedy

#### Scenario: An event type marks individual properties

- **WHEN** a registered event payload type marks some of its properties for encryption and not the
  type
- **THEN** the host starts

#### Scenario: A command is marked as a whole

- **WHEN** a registered command type carries the encryption marking on the type itself
- **THEN** the host starts and the command is encrypted as a whole as before

### Requirement: A recorded field change is protected as the property it names

Where a field-changed event records a new value for an aggregate property marked for encryption,
the value SHALL be stored and published as ciphertext at that property's sensitivity level, for
the stream's owner — the tenant and user the stream's other protected fields and its snapshot are
encrypted for. The name of the changed property SHALL remain readable.

#### Scenario: A change to a marked property is appended

- **WHEN** a field-changed event for a property marked for encryption is appended to a stream
- **THEN** the persisted payload holds ciphertext for the recorded value and the property name in
  the clear, and the value is in the clear nowhere in that payload

#### Scenario: The change is published

- **WHEN** a field-changed event for a property marked for encryption is delivered to a consumer of
  the stream's events
- **THEN** the delivered payload carries the recorded value as ciphertext, as it is stored —
  verified on the mapping of a delivered message, not against a running broker

#### Scenario: The change uses the property's level and the stream's owner

- **WHEN** a property is marked at a given sensitivity level and a change to it is recorded in a
  stream owned by a tenant and user
- **THEN** the recorded value is encrypted under the key that level and owner select, the same key
  the property's value in the aggregate's snapshot is encrypted under

#### Scenario: The owner is erased

- **WHEN** the subject a recorded change was encrypted for is erased, or its key version revoked
- **THEN** the recorded value can no longer be recovered from the stored or the published payload

#### Scenario: A change to an unmarked property is appended

- **WHEN** a field-changed event for a property that is not marked for encryption is appended
- **THEN** it is stored as ordinary data and no key lookup is performed

#### Scenario: The change is appended directly

- **WHEN** a consumer appends a field-changed event for a marked property itself, rather than
  through a change set
- **THEN** the recorded value is protected in the same way

### Requirement: A protected recorded value is bound to the property it names

A protected value in a field-changed event SHALL decrypt only as the value of the property the event
names. Altering the stored property name SHALL make decryption fail rather than yield the value under
another property.

#### Scenario: The property name is altered in storage

- **WHEN** the property name of a stored field-changed event carrying a protected value is changed
  and the event is read
- **THEN** reading the event fails rather than assigning the decrypted value to the other property

#### Scenario: The property name is unchanged

- **WHEN** a stored field-changed event carrying a protected value is read with its key available
- **THEN** the recorded value is recovered

### Requirement: Field changes recorded in the clear are read as they are and not rewritten

A field-changed event recorded in the clear for a property that is marked for encryption — written
before the property was marked, or by a version that did not protect recorded changes — SHALL be read
as it is stored. The framework SHALL NOT rewrite a recorded event to protect it.

The documentation SHALL state that erasing a key does not reach such a value, and SHALL describe how a
consumer finds the affected events and what options it has.

#### Scenario: An earlier plaintext change is read

- **WHEN** a field-changed event whose value was recorded in the clear is read for a property that is
  now marked for encryption
- **THEN** the value is read as ordinary data rather than rejected

#### Scenario: The framework is upgraded

- **WHEN** a host starts on this version against a store holding field changes recorded in the clear
- **THEN** those events are left exactly as they were recorded

#### Scenario: A property is no longer marked

- **WHEN** a field-changed event whose value was recorded as ciphertext is read after the property's
  mark was removed
- **THEN** the value is still decrypted and read

### Requirement: The upgrade order for protected recorded changes is documented

The documentation SHALL state that a host on a version that did not protect recorded field changes
cannot read one that is protected, and that every host reading a stream or consuming its events is
to be upgraded before any host writes to it.

#### Scenario: A consumer plans an upgrade

- **WHEN** a consumer reads the encryption documentation before upgrading hosts that share a store or
  an event subscription
- **THEN** it finds the reader-first upgrade order and the failure an earlier reader shows

### Requirement: A type with marked fields follows its JSON attributes

A type stored field by field, because a property is marked for encryption, SHALL honour its
`System.Text.Json` attributes when written as it does when read. The stored form SHALL keep each
property under its own name, so payloads already stored still read and an upcaster sees the names it
always saw.

#### Scenario: A property is renamed

- **WHEN** a property carrying `[JsonPropertyName]` on such a type is written and read back, whether or
  not any value in the payload is encrypted
- **THEN** its value is recovered, and the stored form holds it under the property's own name

#### Scenario: A property is ignored

- **WHEN** a property carrying `[JsonIgnore]` on such a type is written, unconditionally or under a
  condition its value meets
- **THEN** it is not stored

#### Scenario: A property has its own converter

- **WHEN** a property carrying `[JsonConverter]` on such a type is written
- **THEN** it is stored in the converter's form, and that form is what an upcaster sees

#### Scenario: A payload was stored before the attributes were followed

- **WHEN** a payload stored under the property names, with no property left out, is read
- **THEN** it reads as before, and ordinary JSON under the attributes' names also still reads
