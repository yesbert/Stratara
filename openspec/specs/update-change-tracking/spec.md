# update-change-tracking Specification

## Purpose
Turn "the user edited this form" into the smallest set of facts that actually changed, so that two
people editing different fields of the same record do not overwrite each other — and so the event
stream records what changed rather than what was submitted.

## Requirements

### Requirement: An update is compared three ways, not two

Producing the changes for an update SHALL compare three states: the aggregate as it stood at the
version the editor started from, the aggregate as it stands now, and the values the update submits.

Comparing only the submitted values against the current state cannot tell an intended change from a
stale value the editor never touched — which is exactly how a concurrent edit gets silently
reverted.

#### Scenario: The editor changed a field nobody else touched

- **WHEN** a field differs between the submitted values and the state the editor started from, and
  is unchanged since
- **THEN** the submitted value is taken

#### Scenario: Someone else changed a field the editor did not touch

- **WHEN** a field is unchanged between the submitted values and the state the editor started from,
  but has changed since
- **THEN** the current value is kept — the editor's stale copy does not overwrite it

#### Scenario: Both changed the same field

- **WHEN** a field was changed both by the editor and by someone else since the editor started
- **THEN** the submitted value wins, and the discarded current value is reported as a difference so
  the caller can surface the conflict

#### Scenario: Nothing changed

- **WHEN** the submitted values match the state the editor started from and nothing changed since
- **THEN** no change is produced for that field

### Requirement: Only changed fields become events

Applying a change set SHALL append one event per changed field, and SHALL append nothing where the
change set is empty.

#### Scenario: Some fields changed

- **WHEN** a change set contains changes
- **THEN** one field-changed event is appended per change, and the batch is saved

#### Scenario: Nothing changed

- **WHEN** a change set is empty
- **THEN** nothing is appended and nothing is saved, and the no-op is recorded at debug level

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

### Requirement: Only properties present on both sides participate

A property SHALL participate in the comparison only where it exists on both the submitted values and
the aggregate, with compatible types and a settable target. Everything else SHALL be ignored.

Types are compatible where a submitted value can be assigned to the property, whether or not either
side allows a value type to be absent, or where both are sequences whose elements can be assigned. A
property is settable where it has a setter of any visibility.

#### Scenario: The update carries a field the aggregate does not have

- **WHEN** the submitted values include a property the aggregate does not declare
- **THEN** it is ignored rather than producing a change for a property that cannot be set

#### Scenario: The aggregate has a field the update does not carry

- **WHEN** the aggregate declares a property the submitted values do not
- **THEN** it is left alone — an absent field is not an instruction to clear it

#### Scenario: The types do not match

- **WHEN** a property exists on both sides with incompatible types
- **THEN** it is skipped

#### Scenario: The target property cannot be written

- **WHEN** a property exists on both sides but cannot be set
- **THEN** it is skipped

#### Scenario: The types differ only in whether a value can be absent

- **WHEN** a property exists on both sides and the types differ only in that one side allows a value
  type to be absent
- **THEN** it takes part

#### Scenario: A sequence of another shape is submitted

- **WHEN** the update carries a sequence for a property that is a sequence of another shape, and the
  elements can be assigned
- **THEN** it takes part, and the two are compared by content

#### Scenario: The target property has a non-public setter

- **WHEN** a property exists on both sides and the aggregate's property has a non-public setter
- **THEN** it takes part

### Requirement: Absent values are compared, not skipped

A property whose value is absent SHALL take part in the comparison like any other, so that clearing
a field is a change and an absent value is not mistaken for "no opinion".

The one exception is a value a replay could not apply: an absent value submitted for a property that
cannot be absent SHALL be left out of a change set.

#### Scenario: The editor clears a field

- **WHEN** a field that had a value is submitted with none
- **THEN** that is a change

#### Scenario: A field was and remains absent

- **WHEN** a field is absent in both the submitted values and the state the editor started from
- **THEN** no change is produced

#### Scenario: An absent value is submitted for a property that cannot be absent

- **WHEN** a change set is built from an update whose value is absent for a property of the aggregate
  that has no absent state
- **THEN** no change is produced for that property

### Requirement: An update names the aggregate and the version it started from

An update SHALL carry the identity of the aggregate it targets and the version the editor's copy was
taken at, because the three-way comparison cannot be performed without the second.

#### Scenario: An update is processed

- **WHEN** an update is processed
- **THEN** the aggregate is rebuilt both at the version the update names and at its current version

#### Scenario: The aggregate does not exist

- **WHEN** an update names an aggregate that cannot be rebuilt at either version
- **THEN** it fails, naming the aggregate type and identity, rather than producing a change set
  against nothing

### Requirement: Comparison and access are compiled, not reflective per call

Property reading, writing and comparison SHALL be performed through compiled accessors cached per
type, rather than by reflection on every invocation.

#### Scenario: Many updates are processed

- **WHEN** updates are processed repeatedly for the same type
- **THEN** the accessors are prepared once and reused

### Requirement: Collections are compared by what they hold

Lists, arrays, sets and dictionaries SHALL be compared by content rather than by identity, so a
collection the editor did not touch does not count as edited and cannot overwrite a concurrent change
to it. Any other type SHALL keep its own equality.

#### Scenario: An unedited collection arrives with an update

- **WHEN** a list or array is submitted holding the same elements in the same order as the state the
  editor started from
- **THEN** no change is produced for it, and a merge keeps the current value

#### Scenario: A list differs

- **WHEN** a list or array is submitted with other elements, or the same elements in another order
- **THEN** that is a change

#### Scenario: A set holds the same elements in another order

- **WHEN** a set is submitted holding the same elements in another order
- **THEN** no change is produced for it

#### Scenario: A dictionary differs

- **WHEN** a dictionary is submitted with a different value for a key, or with a key only one side
  recognises
- **THEN** that is a change

### Requirement: A merged update holds only what its properties can hold

Merging an update against the current state SHALL produce a payload without failing on a property
whose type cannot hold the aggregate's value, and SHALL NOT replace a submitted value with an absent
one the payload's property cannot hold.

#### Scenario: The payload's property cannot hold the aggregate's value

- **WHEN** a property of the payload has a type that cannot hold the aggregate's value of that name
- **THEN** the submitted value is copied through unchanged and no difference is reported

#### Scenario: The current value is absent and the payload's property cannot be

- **WHEN** the editor did not touch a field, its current value is absent, and the payload's property
  has no absent state
- **THEN** the submitted value stays

### Requirement: A field change gives the same result in memory and on replay

Applying a field change SHALL set the named property to the same value whether the change was just
made or was read back from the store. A collection of another shape than the property's SHALL be
converted the way a recorded one is read back.

#### Scenario: An array is applied to a list property

- **WHEN** a field change carrying an array is applied in memory to a property that is a list
- **THEN** the property holds a list of those elements, as it would after a replay

#### Scenario: The property holds untyped JSON

- **WHEN** a recorded field change is replayed for a property whose type is untyped JSON
- **THEN** the property holds the recorded JSON value
