## ADDED Requirements

### Requirement: A replay applies each stream in the order it was written

A replay SHALL apply each stream's entries in version order, whatever order the store's sequence
numbers put them in, so that a stream's creating fact is never applied after a fact that follows
it. A save does not number the entries it writes in version order, so the sequence number alone
SHALL NOT decide the order within a stream. Entries of different streams MAY be applied in the order
the store's sequence gives them.

A replay batch SHALL NOT end while a stream it holds still has an entry of a lower version beyond
the batch. Such a batch SHALL be extended until none is left, so a batch MAY be larger than the
configured size. Every entry of the store SHALL be applied exactly once by a replay that completes,
however the batches fall.

The order SHALL come from reading, not from rewriting: a store whose existing entries carry
sequence numbers that contradict their versions SHALL be replayed correctly without migrating a
single entry.

#### Scenario: A commit's sequence numbers run against its versions

- **WHEN** three versions of one stream were saved together and the store numbered them in the
  reverse order, and a replay runs
- **THEN** the projections receive the three versions in version order, and the replay completes
  — verified on the SQLite and the PostgreSQL store

#### Scenario: A batch boundary falls inside the inverted run

- **WHEN** the configured batch size ends a batch between the versions of such a stream
- **THEN** the batch is extended to hold them, the projections receive them in version order, and
  every entry of the store is applied exactly once — verified on the SQLite and the PostgreSQL store

#### Scenario: Several streams are inverted in one batch

- **WHEN** a batch holds two streams, each with a commit whose sequence numbers run against its
  versions, and their entries are interleaved
- **THEN** each stream's entries are applied in version order, and the order between the two
  streams follows the store's sequence

#### Scenario: A store with no inversion is replayed

- **WHEN** every stream's sequence numbers already follow its versions
- **THEN** the replay applies the entries in exactly the order it applied them before, in batches of
  the configured size
