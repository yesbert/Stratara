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

On a relational store, the declaration SHALL also include what reading the store in sequence order
and chaining it need: the event stream indexed by its sequence number, the entries not yet chained
indexed apart from those that are, and the integrity anchors indexed by their sequence number. These indexes SHALL be declared
whichever primary key the consumer's write context gives the event stream, including the key the
framework's own entity-key convention gives it, and SHALL NOT be declared twice over the same column
where that key already is the sequence number. They SHALL carry fixed names, which the documentation
states, so that a consumer that declared the same indexes itself under those names migrates to the
framework's without either index being created twice.

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

#### Scenario: A consumer applies the framework's entity-key convention to its write context

- **WHEN** a consumer applies `ApplyGlobalEntityConfiguration()` to a write context derived from the
  framework's, after the framework's own configuration
- **THEN** the event stream is keyed by its identifier, and the model still declares a unique index
  on its sequence number, an index on the sequence number of the entries not yet chained, and an
  index on the integrity anchors' sequence number

#### Scenario: A consumer keeps the framework's key on the event stream

- **WHEN** a consumer's write context leaves the event stream keyed by its sequence number
- **THEN** the model declares no second index over the sequence number alone, and still declares
  the index of the entries not yet chained and the anchors' index

#### Scenario: A consumer names its schema in another convention

- **WHEN** a write context maps the event stream's columns under a naming convention other than the
  one the framework's registrations apply
- **THEN** the index of the entries not yet chained selects them by the column that context maps the
  chain hash to, and a migration from that model applies — verified on PostgreSQL with snake_case
  and PascalCase names

#### Scenario: A consumer that bridged the indexes adopts the framework's

- **WHEN** a consumer whose own model declared these indexes under the documented names removes its
  declarations and generates a migration
- **THEN** the migration creates and drops no index on the event stream's sequence number or on the
  anchors' sequence number

## ADDED Requirements

### Requirement: Reading one stream costs what the stream holds

Every read the store makes of one stream — whether it exists, its first entry, a range of its
versions, its current version — and every read of one stream's snapshot SHALL be confined to the
stream's partition, which the store works out from the stream's identifier as it does when it
writes. The schema the framework declares SHALL serve each of these reads by an index, so that the
cost of rebuilding an aggregate depends on what its stream and its snapshot hold and not on how many
other streams the store holds. What each read returns, and in what order, SHALL NOT change for a store
the releases from 3.0.0 on wrote. Releases before 3.0.0 worked the partition out differently. For a
store they wrote, the documentation SHALL give a check that finds the rows lying
outside their stream's partition and a repair that moves them there, needing nothing but the database.

#### Scenario: An aggregate is loaded from a large store

- **WHEN** an aggregate whose stream holds a few events is rebuilt from a store holding hundreds of
  thousands of events of other streams
- **THEN** the reads of its stream and of its snapshot are answered by an index over partition,
  stream and version, without a scan of the whole index or the whole table — verified on PostgreSQL
  17 by the plan of each read

#### Scenario: A stream is read on a store that was written before the change

- **WHEN** a store holds streams written by a release of the framework from 3.0.0 on
- **THEN** each read returns the entries and the snapshot it returned before, because every entry
  and every snapshot was already written to the partition its stream identifier gives

#### Scenario: A store holds rows a release before 3.0.0 wrote

- **WHEN** a store holds entries or snapshots under the partition an earlier formula gave their
  stream, and the host runs the documented check
- **THEN** the check counts them, the documented repair moves them to their stream's partition
  without breaking the hash chain, and afterwards every read of those streams returns all their
  entries and their snapshot — verified on PostgreSQL 17, together with the documented formula
  giving the framework's partition for every one of 20,000 stream identifiers
