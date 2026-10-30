## MODIFIED Requirements

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

## ADDED Requirements

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
