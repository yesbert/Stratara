## MODIFIED Requirements

### Requirement: A field-changed event names the field and carries its new value

Each event produced SHALL identify the property that changed and its new value, so that replay can
reconstruct the aggregate by setting that property.

Where the property is marked for encryption, the new value SHALL be carried protected as the property
is, and replay SHALL set the property to it while its key is available and to an absent value once the
key is destroyed, continuing with the next event.

#### Scenario: A field-changed event is replayed

- **WHEN** a field-changed event is applied during rebuilding
- **THEN** the named property is set to the recorded value

#### Scenario: The event names a property the aggregate does not have

- **WHEN** a field-changed event names a property that no longer exists on the aggregate
- **THEN** applying it fails rather than being silently ignored

#### Scenario: A change to a protected property round-trips through the store

- **WHEN** an update changes a property marked for encryption, the change set is applied, and the
  aggregate is rebuilt from the stored events
- **THEN** the rebuilt property holds the submitted value, and the stored event does not hold it in
  the clear

#### Scenario: A protected change is replayed after its key is destroyed

- **WHEN** a stream holding a field-changed event for a property marked for encryption is replayed
  after that value's key was revoked or its owner erased
- **THEN** the property is set to an absent value — null, or the default of a non-nullable value
  type — and the replay continues with the next event

#### Scenario: A protected change recorded in the clear is replayed

- **WHEN** a field-changed event recorded in the clear by an earlier version is replayed for a
  property that is now marked for encryption
- **THEN** the named property is set to the recorded value
