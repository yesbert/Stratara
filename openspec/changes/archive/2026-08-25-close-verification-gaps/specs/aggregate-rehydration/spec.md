## MODIFIED Requirements

### Requirement: An aggregate's state must be restorable from its serialized form

An aggregate SHALL expose its state so that it can be written to a snapshot and read back — its
properties must be settable from outside the type. Where a registered aggregate declares a property
that holds state and cannot be set from outside, the host SHALL refuse to start, naming the
aggregate and the property.

Refusing to start is the point. The failure this replaces is silent: the aggregate rebuilds, the
events after the snapshot apply, and only the state the snapshot held is missing — so the damage
grows the better snapshotting works and nothing reports it.

A property that holds no state of its own — one computed from other properties — is unaffected,
because a restore that omits it loses nothing.

#### Scenario: An aggregate is restored from a snapshot

- **WHEN** an aggregate whose properties are publicly settable is restored from a snapshot
- **THEN** its state matches what was captured

#### Scenario: A property cannot be set from outside

- **WHEN** a registered aggregate declares a state-holding property that cannot be set from outside
  the type
- **THEN** the host fails to start, naming the aggregate and the property, rather than starting and
  losing that property's state on the next snapshot restore

#### Scenario: A property is computed rather than stored

- **WHEN** a registered aggregate declares a property computed from its other properties, with no
  setter
- **THEN** the host starts — the property is recomputed after a restore and nothing is lost
