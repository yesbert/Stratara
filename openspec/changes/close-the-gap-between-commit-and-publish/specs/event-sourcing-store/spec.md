## MODIFIED Requirements

### Requirement: A successful save publishes what was written

A save that persists events SHALL publish them onward as one bundle carrying the session that
produced them, so that read models and process managers see the batch as it was committed.

On a host that has opted in to durable bundles (`outbox-and-messaging` → *Dispatch attempts the bus
first and falls back to durable storage*), the bundle SHALL be written to durable storage in the
transaction that commits the events, so that a save either persists both or neither, and the save
SHALL NOT fail after the commit because the bundle could not be recorded.

#### Scenario: A batch is saved

- **WHEN** a save persists a batch of events
- **THEN** a bundle covering exactly those events is handed to the outbox, carrying the session
  context under which they were written

#### Scenario: A batch is saved with durable bundles

- **WHEN** a save persists a batch of events on a host with durable bundles
- **THEN** the bundle is durable in the same commit as the events, and a failure to record it fails
  the save before anything is committed — verified on the PostgreSQL store

#### Scenario: No session is set

- **WHEN** a save is attempted with no session context
- **THEN** it fails rather than publishing a bundle with no attributable origin
