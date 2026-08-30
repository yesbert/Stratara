## MODIFIED Requirements

### Requirement: A replay truncates every read model before rebuilding

A replay SHALL mark itself active, empty every registered read model, then replay the whole event
stream from the beginning in batches, and mark itself inactive when it finishes — whether it
succeeded or not.

The active marking SHALL be held for a bounded period that the replaying host renews while it works,
so that a replay whose host stops without marking itself inactive ceases to be marked active without
operator intervention. The period SHALL be configurable and SHALL default to a value that outlasts a
slow batch, because a marking that lapses while its replay is still running would let suppressed
publication resume mid-rebuild.

Truncation is what makes a replay a rebuild rather than a re-application: without it, events would
be applied a second time on top of state that already reflects them.

#### Scenario: A replay runs to completion

- **WHEN** a replay runs over a non-empty stream
- **THEN** it activates, truncates every read model, replays the stream in batches, records how many
  events it replayed, and deactivates

#### Scenario: The stream is empty

- **WHEN** a replay runs over an empty stream
- **THEN** it still truncates the read models and still deactivates — a rebuild from nothing produces
  nothing, not the previous contents

#### Scenario: A replay fails partway

- **WHEN** a replay fails after truncating
- **THEN** it deactivates regardless, and the read models are left in whatever partial state the
  replay reached

#### Scenario: A replay's host stops without deactivating

- **WHEN** the host running a replay stops without the replay marking itself inactive
- **THEN** the active marking lapses once it is no longer renewed, and publication is no longer
  suppressed

#### Scenario: A replay is still working

- **WHEN** a replay is between batches and has not finished
- **THEN** the active marking is renewed, so it does not lapse while the replay is still running
