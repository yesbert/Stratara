# Read a stream without walking the store

> **Status:** approved

## Why

Loading an aggregate on the Entity Framework store costs more the more the store holds. This applies to every
aggregate, on every command. The reads of one stream and of its snapshot condition only on the stream id. The
indexes that serve them start with the partition, though, and PostgreSQL before 18 cannot skip a leading column, so
every such read walks the whole index. On top of that, every host that applies the framework's
`ApplyGlobalEntityConfiguration()` to its write context loses the index on the sequence number. That convention
re-keys every entity to `Id`, the event stream included, whose key the framework had put on the sequence number.
After that, the chaining worker, the readers that read the store in sequence order and the store's head are all
sequential scans. The latest-anchor read sorts every anchor on every pass. Nothing is ever pruned, so these costs
only grow.

A consumer reported it on 2026-10-27 against 4.4.10 (consumer finding SF-012). Under the same load, one call rose
from 17 to 52 ms while the store grew to about 1.3 GB. On 300,000 events, reading a stream of three read 2,404
pages in 8.8 ms, and with the partition in the condition it read 6 pages in 0.014 ms. The chaining worker's two
reads were sequential scans of the whole table, 29 and 47 ms per pass. Both consumers on the Entity Framework store
apply the convention. One of them has added the missing indexes in its own migration, and the other has not.

## What Changes

The effect a consumer sees is this: rebuilding an aggregate costs what its stream and its snapshot hold, however
large the store grows, and the chaining worker reads only what still needs chaining. This holds whichever key
convention the host applies to its write context.

- **Stream and snapshot reads carry the partition.** Every read of one stream reads it within its partition, which
  the framework works out from the stream id as it does when it writes. Those reads are: whether the stream exists,
  its first entry, a range of its versions and its current version. The same goes for the reads of one stream's
  snapshot, the latest one and the latest one's version. The existing unique index then serves each of them. This
  part needs no migration.
- **The sequence number stays indexed under any key.** Where the event stream's key is not the sequence number
  (`ApplyGlobalEntityConfiguration()` makes it `Id`), the write model declares a unique index on the sequence
  number. Where the key is the sequence number, it declares nothing more, so a host that never applied the
  convention gets no second index over the same column.
- **Unchained entries have an index of their own.** The write model declares a partial index on the sequence
  number over the entries that are not chained yet. The chaining worker then reads only those, wherever the key is.
- **The latest anchor is found by index.** The write model declares an index on the anchors' sequence number.
- **The new indexes have fixed names:** `ix_event_stream_entry_sequence_number`,
  `ix_event_stream_entry_unhashed_sequence_number` and `ix_event_chain_anchor_sequence_number`. These are the names
  a consumer that bridged the defect already uses. Its migration is then empty once it removes its own
  declarations, and nothing is created twice.
- **Every host on the Entity Framework store generates a migration** for the new indexes. A host that does not
  applies a model with pending changes, as with any schema addition before. The schema documentation says how a large
  store creates these indexes without blocking writes.
- **Unchanged:** what any read returns and in what order, the uniqueness constraints, the tables and columns,
  `ApplyGlobalEntityConfiguration()` itself, and every public type and member. Commit-order reading, the partition
  counter and the Orleans execution model are untouched too.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: *The store declares its own schema* gains the indexes for reading in sequence order, for
  finding unchained entries and for finding the latest anchor. The declaration holds whichever key convention the
  host applies afterwards. A new requirement, *Reading one stream costs what the stream holds*, covers the event and
  snapshot reads of one stream.
- `tamper-evident-streams`: *Chaining happens behind the write, not during it* gains that a pass costs what is
  waiting to be chained, not what has been chained already. *The chain head is anchored periodically* gains that
  finding the last anchor does not read every anchor.

## Impact

- **Code:** `Stratara.EventSourcing.EntityFrameworkCore`. The event-stream and snapshot repositories add the
  partition to their single-stream reads. The write context gains a model-finalizing convention. It declares the
  sequence-number index where the sequence number is not the key, and the partial index for unchained entries, with
  the filter written in the column name and quoting of the model's provider. The anchor configuration gains its
  index. No public type, member or option changes.
- **Schema:** three indexes, no table and no column. Hosts generate an EF Core migration.
- **Tests:** model tests in `tests/Stratara.EntityFrameworkCore.Tests` cover the declared indexes under both keys
  and both naming conventions, plus the SQL of the stream reads. A PostgreSQL test in
  `tests/Stratara.Orleans.IntegrationTests` checks the read plans: index scans, not sequential scans.
- **Documentation:** `docs/getting-started/di-composition.md` (*The store declares its own schema*),
  `docs/concepts/performance-and-scaling.md` (*Horizontal scaling via stream buckets*), the migration note in
  `CHANGELOG.md`.
- **Release:** by precedent a minor, because every host on the Entity Framework store generates a migration, as for
  4.1.0, 4.2.0 and 4.4.0. The stream reads alone would be a patch. The owner decides.
- **Consumer finding:** HiveWeaver `stratara-findings.md`, SF-012. Once it adopts the release, HiveWeaver drops its
  bridging indexes on `(stream_id, version)` and declares none of its own on the sequence number.
- **Superseded sources:** none.
