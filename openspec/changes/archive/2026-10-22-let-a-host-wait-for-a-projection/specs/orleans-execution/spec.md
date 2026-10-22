## ADDED Requirements

### Requirement: A host can wait until a projection has applied what was committed

Where a host has registered the Orleans execution model for projections, it SHALL be able to take a
mark of the store and to wait, for at most a time it names, until a projection it names — by the name
the framework gives it or by its type — has applied every entry the mark covers. A mark SHALL cover
every entry committed at the moment it is taken, in every partition, including an entry that is
committed and visible while another write is still open; it SHALL NOT be required to cover an entry
whose transaction had not committed when the mark was taken. A mark SHALL be opaque to the host: the
host passes it back and relies on nothing inside it.

A wait SHALL answer that the projection reached the mark only when, in every partition, the projection
has applied every entry the mark covers, so that a read of the projection's read model after that
answer reflects every one of them. Where the time passes first, the wait SHALL answer that it did not
reach the mark, SHALL name the partitions that are behind, and SHALL say for them which of these holds:
a partition is stopped — at an entry it cannot apply, or because it cannot read the store; its readers
are suspended for a rebuild or a full replay; or it is only behind. A wait SHALL NOT end early because
a partition is stopped, since a retry may still apply the entry within the time. A rebuild or a full
replay that returns the projection to the beginning during a wait SHALL be waited through: the wait
answers that it reached the mark only once the projection has applied the mark's entries again. A mark
taken while the host read the store under another reader or another partition count than it does when
the mark is passed back SHALL be answered at once as not applicable to the store, never as reached. A
projection that is not registered SHALL be refused with a message naming it. A cancelled wait SHALL end
as cancelled, with no answer. A host that does not use the wait SHALL see no change in how its
projections read and apply.

The documentation SHALL say how a host waits for its own write — take the mark after the write has
committed, then wait — what a mark does not cover, and what each answer means.

#### Scenario: A host waits for its own write

- **WHEN** a host commits a fact, takes a mark after the commit returned, and waits for a projection
  that handles the fact
- **THEN** the wait answers that it reached the mark, and the projection's read model holds the fact
  when the host reads it — verified on the PostgreSQL store with the native reader and on SQLite with
  the portable reader

#### Scenario: Another write is open when the mark is taken

- **WHEN** a host's fact is committed while another transaction that appends to the store is still
  open, and the host takes a mark and waits
- **THEN** the wait does not answer that it reached the mark before the projection has applied the
  host's fact, and answers that it reached it once the other transaction has ended and the fact is
  applied — verified on the PostgreSQL store with the native reader

#### Scenario: The projection does not get there in time

- **WHEN** a projection is behind the mark when the wait's time passes
- **THEN** the wait answers that it did not reach the mark and names the partitions that are behind;
  it does not answer that it reached it

#### Scenario: A partition is stopped at an entry

- **WHEN** a projection keeps failing on an entry the mark covers until the wait's time passes
- **THEN** the wait answers that the projection is stopped and names that partition

#### Scenario: The projection is suspended

- **WHEN** a projection's readers are paused for a rebuild, or a full replay is active, until the
  wait's time passes
- **THEN** the wait answers that the projection is suspended and names the partitions

#### Scenario: The projection is rebuilt during the wait

- **WHEN** a projection is rebuilt while a host waits for it
- **THEN** the wait answers that it reached the mark only after the rebuilt read model holds every
  entry the mark covers

#### Scenario: The store has nothing in a partition

- **WHEN** a host takes a mark of a store with partitions that hold no entries and waits
- **THEN** those partitions count as reached without waiting for their readers

#### Scenario: The partition count changed after the mark was taken

- **WHEN** a host passes back a mark taken under another partition count or another reader
- **THEN** the wait answers at once that the mark does not apply to the store

#### Scenario: The projection is unknown

- **WHEN** a host waits for a projection that is not registered
- **THEN** the wait is refused with a message naming the projection
