## ADDED Requirements

### Requirement: A projection can apply an event idempotently without masking real conflicts

The framework SHALL offer a way for a projection to apply an event whose effect may already be
present, without failing, and without suppressing a conflict that indicates genuinely concurrent
modification.

The distinction is the whole point: at-least-once delivery means a projection will see the same
event twice, and cascading deletes mean a row may vanish between the read and the write. Neither is
an error. A second writer changing a row that still exists is.

#### Scenario: The event's effect is already present

- **WHEN** a projection applies an event whose effect the read model already reflects
- **THEN** nothing is written, and the bundle continues

#### Scenario: The target of an update no longer exists

- **WHEN** a projection applies an update for a row that has since been deleted
- **THEN** the update is skipped rather than failing — the row's absence is the end state, not a
  fault

#### Scenario: A deletion races another deletion

- **WHEN** a projection deletes a row that a concurrent bundle has already deleted
- **THEN** the deletion is treated as satisfied, because the intended end state has been reached

#### Scenario: A genuine conflict occurs

- **WHEN** a projection's write conflicts with a concurrent modification to a row that still exists
- **THEN** the conflict is **not** suppressed and the bundle fails, as an unhandled projection
  failure does
