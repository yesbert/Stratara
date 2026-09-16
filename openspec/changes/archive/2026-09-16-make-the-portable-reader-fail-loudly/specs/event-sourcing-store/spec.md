## MODIFIED Requirements

### Requirement: The store can be read in commit order without skipping a late committer

The framework SHALL offer a reader that returns a partition's entries after a position, in an order
in which no entry at or below the position of a returned batch can still commit later, so that a
consumer resuming from a stored position never skips an entry. A batch SHALL say whether more
entries exist, and a batch SHALL never end in the middle of one transaction's entries. Two readers
SHALL be offered: one native to PostgreSQL that adds no work to an append, and one for any relational
provider the framework ships a store registration for, under which concurrent appends to one partition
wait for each other. The portable reader SHALL see only entries appended by a process that maintains
its partition counter; every process that appends to a store read by it SHALL maintain the counter, and
a reader that finds an entry appended without it SHALL stop its partition and report the entry rather
than read past it. The partition count of a store read by the portable reader SHALL NOT change without
renumbering its entries, and a host whose partition count is lower than the store's counter rows show
SHALL refuse to start. A position SHALL NOT be accepted by the other reader, nor under a partition count
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

#### Scenario: A process appends without maintaining the counter

- **WHEN** a store is read by the portable reader and a process that does not maintain the partition
  counter appends an entry
- **THEN** the reader of that entry's partition stops before it, reports the unpositioned entry and
  what is missing, and resumes once the entry is positioned — verified on the PostgreSQL store only

#### Scenario: The partition count is lowered under the portable reader

- **WHEN** a host that reads with the portable reader starts with a partition count lower than the
  number of counter rows the store holds
- **THEN** it refuses to start with a message naming both counts, instead of reading merged partitions
  whose positions overlap — verified on the PostgreSQL store only
