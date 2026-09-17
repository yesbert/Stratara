## MODIFIED Requirements

### Requirement: The store can be read in commit order without skipping a late committer

The framework SHALL offer a reader that returns a partition's entries after a position, in an order
in which no entry at or below the position of a returned batch can still commit later, so that a
consumer resuming from a stored position never skips an entry. A batch SHALL say whether more
entries exist, and a batch SHALL never end in the middle of one transaction's entries: a transaction
whose entries would straddle the end of a batch SHALL be held back whole for the next batch, and a
transaction that alone holds more entries than a batch SHALL be returned whole, so that a batch MAY be
larger than the size asked for. A reader SHALL
report a partition's head: the position after which no entry committed at the time of the call exists,
so that a consumer can start there. Two readers
SHALL be offered: one native to PostgreSQL that adds no work to an append, and one for any relational
provider the framework ships a store registration for, under which concurrent appends to one partition
wait for each other. A store that holds entries from before the native reader's commit record was
added SHALL be adoptable without rewriting its event table: the documented migration adds the record
without a rewrite, and the framework's backfill stamps the existing entries in the order they were
appended, in bounded batches each committed on its own, so that the reader returns history in bounded
batches and in append order; the backfill SHALL run while nothing appends, and the documentation SHALL
say so. The portable reader SHALL see only entries appended by a process that maintains
its partition counter; every process that appends to a store read by it SHALL maintain the counter, and
a reader that finds an entry appended without it SHALL stop its partition and report the entry rather
than read past it — whatever the entry's place in its partition and however many entries without a
position other partitions hold. Maintaining the counter is the write context's to declare through the
documented interceptor; the framework SHALL NOT offer a setting that appears to switch the counter on
or off without doing so, and the setting that once did SHALL be marked obsolete with a message naming
the interceptor until it is removed. Positions handed out to one append SHALL follow the version order
within each stream. Positioning an entry that was appended without the counter SHALL NOT change a
position already handed out: the entry SHALL take a position after every position the partition has
handed out, so that a checkpoint written before the positioning stays true and a reader resumes from
it, reads the positioned entry once, and re-applies nothing. The documentation SHALL state that such an
entry is therefore read after entries appended with the counter in the meantime — a later version of
its own stream included — that the process appending without the counter is to be stopped before
positioning, and that a read model which stops on the resulting order is repaired by a rebuild.
The partition count of a store read by the portable reader SHALL NOT change without
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

#### Scenario: One transaction holds more entries than a batch

- **WHEN** one transaction appends more entries to a partition than the batch size a consumer reads
  with, and other transactions append before and after it
- **THEN** a batch that reaches that transaction returns it whole and larger than the batch size, a
  transaction that would straddle the end of a batch is held back whole for the next one, and
  transactions that fit are cut at the batch size — verified for the native reader on the PostgreSQL
  store

#### Scenario: A reader reports its head

- **WHEN** a consumer asks a reader for a partition's head and then reads after that position
- **THEN** no entry committed before the head was asked for is returned, and an entry committed
  afterwards is — verified for both readers on the PostgreSQL store

#### Scenario: A populated PostgreSQL store adopts the native reader

- **WHEN** a store holding entries written before the commit record existed is migrated as documented
  and backfilled while nothing appends
- **THEN** the event table is not rewritten by the migration, a reader returns the history in batches
  no larger than the backfill's batch and in the order the entries were appended, and a batch never
  holds more than one backfill batch's entries — verified on the PostgreSQL store

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

#### Scenario: A late entry is positioned behind a checkpoint

- **WHEN** a reader holds a checkpoint past several positioned entries, an entry of its partition is
  appended without the counter, and the entry is then positioned
- **THEN** the positioned entries keep their positions, the reader resumes from its checkpoint, reads
  the late entry once after them, and re-applies none of them — verified on the PostgreSQL store only

#### Scenario: Unpositioned entries pile up in other partitions

- **WHEN** many entries are appended without the counter to other partitions after one was appended
  without it to the reader's own
- **THEN** the reader of that partition still stops before its own unpositioned entry, however many the
  others hold — verified on the PostgreSQL store only with more foreign entries than one read of the
  store returns

#### Scenario: One append holds several versions of one stream

- **WHEN** one save appends several versions of one stream, and of another stream, to a store that
  maintains the counter
- **THEN** the positions handed out follow the version order within each stream, and a reader returns
  them in that order — verified on the PostgreSQL store only

#### Scenario: A host sets the retired switch

- **WHEN** a host sets the setting that once claimed to maintain the counter
- **THEN** the build warns that it is obsolete, names the interceptor as what maintains the counter, and
  the value changes nothing
