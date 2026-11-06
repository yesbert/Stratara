## MODIFIED Requirements

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

## ADDED Requirements

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
