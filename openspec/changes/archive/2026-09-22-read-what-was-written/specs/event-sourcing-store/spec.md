## ADDED Requirements

### Requirement: A commit-order reader tolerates the consumer's mapping of the event table

A reader SHALL read an entry through the columns the consumer's own write context maps it to, and
SHALL NOT depend on every column of the event table being returned by a wildcard selection. A write
context that maps a property onto a column a wildcard selection does not return — a provider's
system column among them, which is what the framework's own row-version convention produces on
PostgreSQL — SHALL be read without error.

#### Scenario: The write context applies the framework's row-version convention

- **WHEN** a host applies the framework's row-version convention to a write context, in the mode the
  documentation names for PostgreSQL, and a consumer reads the store in commit order
- **THEN** the entries are returned and the consumer's checkpoint advances — verified on the
  PostgreSQL store with the native reader

### Requirement: Entries committed together are read in stream order

Entries appended by one save SHALL reach a consumer reading the store in commit order with the
entries of each stream in version order, whichever of the two readers is in use, so that a stream's
creating fact is never read after a fact that follows it. Entries of different streams committed
together MAY be interleaved in any order. The documentation SHALL state what a projection may rely
on, so that a projection which stops on a missing preceding fact is a defect in the projection
rather than in the order it was served.

#### Scenario: One save creates a stream and appends to it

- **WHEN** one save creates a stream and appends a further version of it, and both are committed
  together
- **THEN** a consumer reading in commit order receives the creating fact before the fact that
  follows it — verified on the PostgreSQL store for both readers

#### Scenario: One save appends to two streams

- **WHEN** one save appends several versions of each of two streams and commits them together
- **THEN** each stream's entries reach the consumer in version order, whatever order the two
  streams' entries are interleaved in — verified on the PostgreSQL store for both readers

### Requirement: An append without a causation identity is refused before it is committed

Appending to a stream SHALL be refused when the ambient session carries no causation identity,
before anything is written, with an error that names `AddCommandAuditing()` as what supplies one.
The refusal SHALL be the framework's own, not the store's: a consumer SHALL NOT have to read a
database constraint violation to learn that a registration is missing. The documentation for
writing a command handler SHALL state that appending requires that registration, and no documented
example SHALL show a session that cannot append.

#### Scenario: A host appends without the command-audit registration

- **WHEN** a host that has not registered command auditing dispatches a command whose handler
  appends an event
- **THEN** the append is refused before the transaction opens, with a message naming
  `AddCommandAuditing()`, and nothing is written

#### Scenario: A host appends with the command-audit registration

- **WHEN** a host that has registered command auditing dispatches the same command
- **THEN** the event is appended and carries the causation identity of the command that produced it
