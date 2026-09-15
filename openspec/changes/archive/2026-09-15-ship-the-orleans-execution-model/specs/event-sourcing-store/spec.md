## MODIFIED Requirements

### Requirement: The store declares its own schema

The framework SHALL declare the tables it needs — the event stream, snapshots, the command log, the
outbox and the integrity anchors — with the uniqueness and index constraints its guarantees depend
on, so that a consumer's migration produces a schema that enforces them.

The declaration SHALL include what the commit-order readers and the Orleans execution model need:
on the event stream, a commit-order column filled by the database on insert where the provider
offers one, and a per-partition position column with its counter table for every provider; on the
outbox record, the attempt count, the kept state, the time of the last hand-over and the aggregate the
command names, which a bounded resume needs; and on
the read side, a checkpoint table keyed by consumer and partition. A consumer that does not use the
execution model SHALL be able to migrate these additions without any behaviour changing.

#### Scenario: A consumer migrates the store

- **WHEN** a consumer generates a migration from the framework's model
- **THEN** the event stream carries a unique constraint over partition, stream and version, so
  version collision is refused by the database and not only by the application
- **AND** snapshots carry the same uniqueness, and the integrity anchors carry a unique constraint
  over partition and sequence

#### Scenario: A consumer migrates to the version that ships the execution model

- **WHEN** a consumer generates a migration from the framework's model after upgrading
- **THEN** the migration adds the commit-order and position columns, the counter table, the outbox
  record's resume bookkeeping and the checkpoint table, with the indexes the readers use, and a host
  that has not adopted the execution model behaves as before

#### Scenario: A context hosts several of the framework's stores

- **WHEN** a database context is defined alongside sibling contexts in the same assembly
- **THEN** it applies only its own entity configurations — a context that picked up a sibling's
  would produce a model the consumer's migrations do not match, detectable only against a real
  database

## ADDED Requirements

### Requirement: The store can be read in commit order without skipping a late committer

The framework SHALL offer a reader that returns a partition's entries after a position, in an order
in which no entry at or below the position of a returned batch can still commit later, so that a
consumer resuming from a stored position never skips an entry. A batch SHALL say whether more
entries exist, and a batch SHALL never end in the middle of one transaction's entries. Two readers
SHALL be offered: one native to PostgreSQL that adds no work to an append, and one for any relational
provider the framework ships a store registration for, under which concurrent appends to one partition
wait for each other. A position SHALL NOT be accepted by the other reader, nor under a partition count
other than the one it was written under. A store that holds entries from before the portable reader
was adopted SHALL be positioned once before that reader serves, and the reader SHALL refuse to start on
a store with entries it has not positioned.

#### Scenario: A long transaction commits after a later one

- **WHEN** one transaction holds an append open while another appends and commits, and a reader
  reads in between
- **THEN** the entry of the long transaction is returned by a later read and never skipped — verified
  in two hundred randomised interleavings per reader on the PostgreSQL store

#### Scenario: A consumer switches readers

- **WHEN** a checkpoint written under one reader is read under the other
- **THEN** the read is refused with a message naming both readers, rather than misread

#### Scenario: A consumer changes the partition count

- **WHEN** a checkpoint written under one partition count is read after the host changed the count
- **THEN** the read is refused with a message naming both counts, rather than skipping entries

#### Scenario: A store with history adopts the portable reader

- **WHEN** a store holding entries written before the upgrade adopts the portable reader
- **THEN** the documented backfill positions those entries in commit order within each partition, and
  a reader started before the backfill refuses to start with a message that names it
