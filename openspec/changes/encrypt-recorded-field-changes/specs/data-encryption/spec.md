## ADDED Requirements

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
