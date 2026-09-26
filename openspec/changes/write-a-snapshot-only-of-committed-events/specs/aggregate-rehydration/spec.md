## MODIFIED Requirements

### Requirement: Snapshots shorten a replay without changing its result

Where a snapshot exists for a stream, rebuilding SHALL start from that snapshot's state and apply
only the events after it. The result SHALL be the same as replaying from the beginning.

A snapshot SHALL capture only events that were committed. A save that fails — a concurrency conflict
included — SHALL leave no snapshot behind, and a snapshot that cannot be written after the events were
committed SHALL NOT fail the save; it SHALL be logged, and a later save writes one.

#### Scenario: A snapshot exists

- **WHEN** an aggregate is rebuilt and a snapshot exists at some version
- **THEN** the snapshot's state is the starting point, and only events after that version are read
  and applied

#### Scenario: No snapshot exists

- **WHEN** no snapshot exists for the stream
- **THEN** rebuilding replays the whole stream

#### Scenario: Rebuilding is bounded to a version before the snapshot

- **WHEN** rebuilding is bounded to a version earlier than the latest snapshot
- **THEN** a snapshot no later than that bound is used, so the bound is honoured

#### Scenario: A save loses a race while a snapshot is due

- **WHEN** a save whose batch reaches the snapshot threshold fails because another writer committed
  the same versions first
- **THEN** no snapshot of the failed batch exists afterwards, and the stream's rebuilt state is the
  committed one

#### Scenario: The snapshot cannot be written after the commit

- **WHEN** a save commits its events and writing the snapshot then fails
- **THEN** the save succeeds, its events are recorded and published, and the failure is logged
