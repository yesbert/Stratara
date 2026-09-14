## ADDED Requirements

### Requirement: A failing saga does not lose the bundle

Where a saga handler fails, the failure SHALL propagate rather than being swallowed, so the bundle
is not acknowledged on the saga subscription and is not treated as processed. The bundle SHALL then
be delivered again a bounded number of times and, after the bound, moved to the saga subscription's
dead-letter destination as `outbox-and-messaging` → *A message a handler cannot take is retried a
bounded number of times and then kept* states — never discarded.

A saga has no replay. A bundle that vanished from its subscription was a step in a process that
nobody would ever take; keeping the bundle is the only way that process can still complete.

#### Scenario: A saga handler fails

- **WHEN** a saga handler throws while processing a bundle
- **THEN** the failure propagates out of bundle processing and is recorded
- **AND** the bundle is not treated as processed and is delivered again

#### Scenario: A saga handler keeps failing

- **WHEN** a saga handler throws on every delivery of a bundle
- **THEN** the bundle ends on the saga subscription's dead-letter destination, and the saga receives
  it when the operator returns it

#### Scenario: A saga's command conflicts

- **WHEN** the command a saga issues in reaction to a bundle reports a concurrency conflict
- **THEN** the bundle is redelivered under the conflict bound rather than the failure bound
