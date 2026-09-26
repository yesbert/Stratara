## MODIFIED Requirements

### Requirement: A snapshot captures state per stream and aggregate type

Snapshot evaluation SHALL treat each stream and aggregate type in a saved batch independently, and a
written snapshot SHALL record the aggregate's state at the batch's highest version for that stream,
protected under the stream's recorded owner — the tenant and, where one was recorded, the user of the
stream's first event — so that every erasure that reaches the stream's events reaches its snapshots
too. The owner SHALL be the stream's, never a subject stated for an event of the batch that triggered
the snapshot. A snapshot SHALL be read back under the owner it was written under.

#### Scenario: One save touches several streams

- **WHEN** a batch contains events for more than one stream
- **THEN** each stream is evaluated for snapshotting on its own, and a snapshot for one does not
  imply a snapshot for another

#### Scenario: A snapshot is written

- **WHEN** a snapshot is written
- **THEN** it records the aggregate as of the highest version in the batch, serialized under the
  owner recorded on the stream

#### Scenario: A snapshot of a user's aggregate is written

- **WHEN** a snapshot is written for a stream whose first event was recorded for a tenant and a user
- **THEN** it is protected under that tenant and that user, so the user's erasure makes its user-level
  fields unreadable as it does the events'

#### Scenario: The batch that triggers a snapshot starts with a stated subject

- **WHEN** the first event of the triggering batch was appended on behalf of a subject other than the
  stream's owner
- **THEN** the snapshot is still protected under the stream's owner

#### Scenario: A snapshot written before the user was recorded is read

- **WHEN** a snapshot that records no user is read
- **THEN** it is read under its tenant alone, as it was written
