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
indexed apart from those that are, and the integrity anchors indexed by their sequence number. These
indexes SHALL be declared whichever primary key the consumer's write context gives the event stream,
including the key the framework's own entity-key convention gives it, and SHALL NOT be declared twice
over the same column where that key already is the sequence number. They SHALL carry fixed names,
which the documentation states, so that a consumer that declared the same indexes itself under those
names migrates to the framework's without either index being created twice. A write context that
keeps the framework's own model conventions from running SHALL be refused when its model is built,
with an error naming the context and what it has to call, rather than produce a model and a
migration without these indexes.

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

#### Scenario: A write context keeps the framework's conventions from running

- **WHEN** a write context derived from the framework's overrides the hook that configures model
  conventions without calling the framework's implementation, and its model is built at runtime or
  for a migration
- **THEN** building the model fails with an error that names the context and says to call the
  framework's implementation, and no query runs and no migration is generated against that model
