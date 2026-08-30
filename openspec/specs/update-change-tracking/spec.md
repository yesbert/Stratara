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

#### Scenario: A field-changed event is replayed

- **WHEN** a field-changed event is applied during rebuilding
- **THEN** the named property is set to the recorded value

#### Scenario: The event names a property the aggregate does not have

- **WHEN** a field-changed event names a property that no longer exists on the aggregate
- **THEN** applying it fails rather than being silently ignored

### Requirement: Only properties present on both sides participate

A property SHALL participate in the comparison only where it exists on both the submitted values and
the aggregate, with compatible types and a settable target. Everything else SHALL be ignored.

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

### Requirement: Absent values are compared, not skipped

A property whose value is absent SHALL take part in the comparison like any other, so that clearing
a field is a change and an absent value is not mistaken for "no opinion".

#### Scenario: The editor clears a field

- **WHEN** a field that had a value is submitted with none
- **THEN** that is a change

#### Scenario: A field was and remains absent

- **WHEN** a field is absent in both the submitted values and the state the editor started from
- **THEN** no change is produced

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
