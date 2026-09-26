## MODIFIED Requirements

### Requirement: A successful save publishes what was written

A save that persists events SHALL publish them onward as one bundle carrying the session that
produced them, so that read models and process managers see the batch as it was committed. A save
with nothing staged SHALL write and publish nothing.

Where the events were committed but their bundle could not be handed on, the save SHALL fail with a
failure of its own that says the events are committed and names their streams — whatever ended the
handover, a cancellation included. It SHALL be distinguishable from a save that wrote nothing, and
neither the framework's retrying pipelines nor its transports nor its recorded-command resumption
SHALL run the work again because of it, since that would record the same facts a second time.

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
  the save before anything is committed — the atomic commit verified on the PostgreSQL store, the
  refused record on the SQLite store the test-support package registers

#### Scenario: No session is set

- **WHEN** a save is attempted with no session context
- **THEN** it fails rather than publishing a bundle with no attributable origin, with a failure that
  identifies itself as a missing identity and can be caught without reference to the store

#### Scenario: A save with nothing staged

- **WHEN** a save is performed with no event staged since the last one
- **THEN** nothing is written and no bundle is published

#### Scenario: Handing the bundle on fails after the commit

- **WHEN** a save commits its events and handing their bundle on then fails, on a host without
  durable bundles
- **THEN** the save fails with a failure that says the events are committed and names their streams,
  the events remain recorded, and a pipeline of the framework that retries on failure does not retry
  it

#### Scenario: The handover is cancelled after the commit

- **WHEN** a save commits its events and handing their bundle on is then cancelled
- **THEN** the save fails with the same failure, carrying the cancellation, rather than surfacing as a
  plain cancellation
