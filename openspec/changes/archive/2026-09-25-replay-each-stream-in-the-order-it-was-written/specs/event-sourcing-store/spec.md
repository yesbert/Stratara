## ADDED Requirements

### Requirement: Backfilled history is read in version order within each stream

The backfills that prepare a store's existing history for either commit-order reader SHALL leave
that history in a state in which the reader returns each stream's entries in version order, even
where the store's sequence numbers, which a save does not assign in version order, say otherwise.
A backfill batch SHALL NOT end while a stream it holds still has an entry of a lower version,
unprepared, beyond the batch; such a batch SHALL be extended until none is left.

Neither backfill SHALL change a commit record or a position that was handed out before it ran,
whether by an earlier run or by a process maintaining the partition counter. History prepared by a
release that did not keep this guarantee is therefore not revisited. The documentation SHALL say how
to find out whether a store holds a stream whose prepared order contradicts its versions.

#### Scenario: The native backfill meets a commit that straddles its batch

- **WHEN** a PostgreSQL store holds a stream whose versions were numbered in the reverse order, and
  the native reader's backfill runs with a batch size that ends a batch between them
- **THEN** the native reader returns the stream's entries in version order — verified on the
  PostgreSQL store

#### Scenario: The portable backfill positions an inverted commit

- **WHEN** a store holds a stream whose versions were numbered in the reverse order, and the portable
  reader's backfill positions them, with a batch boundary between them and without one
- **THEN** the positions follow the stream's versions, and the portable reader returns them in
  version order — verified on the PostgreSQL store for the positioning PostgreSQL uses, and on
  SQLite for the positioning every other provider uses

#### Scenario: A backfill runs on history it already prepared

- **WHEN** either backfill runs again on a store it has already prepared
- **THEN** it changes nothing, as before
