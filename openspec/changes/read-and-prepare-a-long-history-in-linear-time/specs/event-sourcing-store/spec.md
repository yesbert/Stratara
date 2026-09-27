## MODIFIED Requirements

### Requirement: Backfilled history is read in version order within each stream

The backfills that prepare a store's existing history for either commit-order reader SHALL leave
that history in a state in which the reader returns each stream's entries in version order, even
where the store's sequence numbers, which a save does not assign in version order, say otherwise.
A backfill batch SHALL NOT end while a stream it holds still has an entry of a lower version,
unprepared, beyond the batch; such a batch SHALL be extended until none is left.
The work a backfill batch does — finding where it ends and what extends it — SHALL NOT grow with
the history outside the batch, however large a share of it one long-lived stream holds and whatever
the database estimates of how much of the history is still unprepared, so that a store is prepared
in time that grows with its history rather than with its square.

The portable reader's backfill SHALL prepare every entry the store holds, whatever query filters the
consumer's write context declares: a filter that hides entries from the application SHALL NOT hide
them from the backfill.

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

#### Scenario: One stream holds a large share of the history a backfill prepares

- **WHEN** a store holds a long history through which one stream's entries are spread, some of that
  stream's commits numbered against their versions, and either backfill prepares it with a batch
  size much smaller than the stream
- **THEN** the reader returns each stream in version order, and the entries the backfill reads from
  the store over the whole run stay within a small multiple of the entries the store holds —
  verified on the PostgreSQL store for both backfills

#### Scenario: The write context filters entries by tenant

- **WHEN** the consumer's write context declares a query filter that restricts entries to the
  session's tenant, and the portable reader's backfill runs without a session
- **THEN** it positions the entries of every tenant, in the same order as on a context without the
  filter — verified on the PostgreSQL store for the positioning PostgreSQL uses, and on SQLite for
  the positioning every other provider uses
