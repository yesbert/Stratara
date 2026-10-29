## MODIFIED Requirements

### Requirement: Keys are scoped, and a scope is the unit of erasure

The framework SHALL manage data-encryption keys per scope, where a scope combines a sensitivity
level with the tenant and user the data belongs to. Distinct scopes SHALL receive distinct keys.

Destroying a scope's keys is what makes that scope's data unrecoverable; a key shared across scopes
would make erasure for one subject destroy another subject's data.

Data that belongs to a user rather than to any of their tenants SHALL have a key of that user alone.
Data at the user level written under a session whose data-owner tenant is the reserved default and
whose data-owner user is set SHALL be encrypted under that key. That key SHALL be shredded by the
user's erasure and SHALL NOT be shredded by the erasure of any tenant the user belongs to. The
documentation SHALL describe this as the way to write data that follows a person across tenants, and
SHALL state that the scope is fixed when the data is written: data written under one of the user's
tenants stays bound to that tenant and is shredded with it.

#### Scenario: Two different scopes request a key

- **WHEN** keys are requested for two scopes that differ in tenant, in user, or in sensitivity level
- **THEN** the keys returned are different

#### Scenario: The same scope requests a key twice

- **WHEN** the current key for a scope is requested more than once
- **THEN** the same key and key identity are returned — a scope has one current key, not one per call

#### Scenario: A user's own data is written

- **WHEN** a value at the user level is encrypted under a session whose data-owner tenant is the
  reserved default and whose data-owner user is set
- **THEN** it is encrypted under a key that names the user and no tenant of theirs, and it reads back
  normally under a session of any of the user's tenants

#### Scenario: A user's own data outlives one of their tenants

- **WHEN** a user's own data has been written and a tenant the user belongs to is then erased
- **THEN** the data still reads normally

#### Scenario: A user's own data is erased with the user

- **WHEN** a user's own data has been written and the user is then erased
- **THEN** the data's encrypted fields read as absent

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
