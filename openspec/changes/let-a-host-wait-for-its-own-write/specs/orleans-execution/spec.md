## ADDED Requirements

### Requirement: A host can wait until a projection has applied its own write

Where a host has registered the Orleans execution model for projections, it SHALL be able to take a mark of the streams
it names. Taken after a commit to those streams, the mark SHALL cover every entry committed to them by then, the
host's own commit included. A wait for such a mark SHALL answer that the projection reached it once the projection has
applied those entries in the partitions those streams fall into, however far the projection lags in any other
partition. Every other answer of the wait SHALL be the one a mark of the whole store gets, and a mark of streams SHALL
be passed to the same wait. The documentation SHALL say when a host takes a mark of its streams and when a mark of the
whole store, which a decision across streams needs.

#### Scenario: A host waits for its own write while another partition lags

- **WHEN** a projection is stopped at an entry in one partition, a host commits a fact to a stream of another
  partition, takes a mark of that stream after the commit and waits
- **THEN** the wait answers that it reached the mark once the projection has applied the host's fact, and does not
  wait for the stopped partition — verified on the PostgreSQL store with the native reader and on SQLite with the
  portable reader

#### Scenario: A mark of streams names a stream with nothing committed

- **WHEN** a host takes a mark of a stream nothing has been committed to and waits
- **THEN** the wait answers that it reached the mark at once

#### Scenario: A mark of the whole store still waits for every partition

- **WHEN** a projection is stopped at an entry in one partition and a host takes a mark of the whole store and waits
- **THEN** the wait answers that the projection is stopped in that partition, as before
