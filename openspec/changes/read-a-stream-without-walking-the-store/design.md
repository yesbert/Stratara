## Context

See proposal.md, *Why*, for the defect. The source state at `ae11357` (tag `v4.4.10`):

- `EventStreamEntryConfiguration` keys the event stream by `SequenceNumber` (line 22). It declares a unique index
  over `(BucketId, StreamId, Version)` (line 24). `SnapshotConfiguration` declares the same unique index for
  snapshots (line 20), and `EventChainAnchorConfiguration` declares one over `(BucketId, SequenceNumber)` (line 18).
- `EventStreamRepository.StreamExistsAsync`, `GetFirstOrDefaultAsync`, `GetManyAsync` and `GetVersionOrDefaultAsync`
  (lines 22–48) condition on `StreamId` alone. So do `SnapshotRepository.GetAsync` and
  `GetLatestVersionOrDefaultAsync` (lines 19–42). `FindStragglerAsync` (line 133) and the partition-counter backfill
  already condition on both.
- Every entry and every snapshot is written with `BucketId = BucketCalculator.GetBucketId(streamId)`
  (`EventSource.cs:463`, `SnapshotService.cs:130`). There is no other writer. `BucketCalculator` is a pure function
  of the identifier's bytes, so the partition of a stream can always be worked out from its identifier.
- `ModelBuilderExtensions.ApplyGlobalEntityConfiguration()` calls `HasKey(Id)` on every `IEntity`
  (`ModelBuilderExtensions.cs:107`). `EventStreamEntry` is an `IEntity`. A consumer calls the method after
  `base.OnModelCreating`, which is where `WriteDbContext` applies the framework's configurations. The call
  therefore replaces the key on the sequence number, and the sequence number keeps no index at all. Both consumers
  on the Entity Framework store do this: the HiveWeaver migration names `pk_event_stream_entry` on `id`, and NextPA's
  write context ends in the same call.
- On the sequence number, the chaining worker reads `GetUnhashedEventsAsync`, `GetPreviousEventAsync` and
  `GetLastHashedEventAsync`. The sequence-order readers read `GetManyAfterSequenceAsync` and
  `GetManyAfterSequenceInStreamOrderAsync`, and the store's head is `GetMaxSequenceNumberAsync`.
  `EventChainService` reads `GetLastSequenceNumberOrDefaultAsync([])` on every pass. Anchors are written about every
  5 events (`AnchorRange`).
- The framework's PostgreSQL registrations apply snake_case naming, and so do both consumers' design-time
  factories. The integration tests also run a PascalCase write context (`NativeReaderModelNamesTests`).

## Goals / Non-Goals

**Goals:**

- Each read of one stream or of its snapshot is served by the unique index that already exists, and needs no
  migration.
- The sequence number and the anchors' sequence number are indexed in the final model whatever key the consumer
  leaves on the event stream, with no index declared twice.
- The indexes a consumer has already bridged, declared under the same names and definitions, produce an empty
  migration once that consumer removes its own declarations.

**Non-Goals:**

- Changing `ApplyGlobalEntityConfiguration()`, or the key of an existing consumer table (D2).
- Creating indexes concurrently from the framework's model (D5).
- Supporting a key convention other than the two named here. Any key that is not the sequence number gets the
  index; that is the whole test.
- Pruning, partitioning or archiving the event table.

## Decisions

### D1 — The single-stream reads compute the partition and condition on it

Each of the six reads computes `var bucketId = BucketCalculator.GetBucketId(streamId)` and conditions on
`e.BucketId == bucketId && e.StreamId == streamId`. The order and the projection of each query stay as they are.
The unique index over `(bucket_id, stream_id, version)` then serves equality on its first two columns and the
range or order on the third. The snapshot read's type-name condition is evaluated over the few rows of that range.

*Alternatives rejected:*
- Declaring an index over `(stream_id, version)`, which is how HiveWeaver bridged the gap. That is a second B-tree
  over the same rows, kept up on every append, and it duplicates an index that already exists and would serve the
  reads once the condition is complete.
- Relying on skip scan in PostgreSQL 18. Hosts run older versions, and with 4,096 partitions a skip scan still
  descends the index once per partition.

*Evidence:* the measurement in consumer finding SF-012 (2,404 pages in 8.8 ms against 6 pages in 0.014 ms on
300,000 events), and the implementation of the write path above.

### D2 — The sequence-number indexes come from a model-finalizing convention on the write context

`WriteDbContext<TContext>` overrides `ConfigureConventions` and adds an internal `IModelFinalizingConvention`. When
the model is finalized, after every `OnModelCreating` and after the consumer's `ApplyGlobalEntityConfiguration()`,
the convention looks at the event stream's primary key:

- If the key is not exactly `[SequenceNumber]`, it declares a unique index on `SequenceNumber`, named
  `ix_event_stream_entry_sequence_number`.
- In either case, it declares a named index on `SequenceNumber`, `ix_event_stream_entry_unhashed_sequence_number`,
  filtered to the entries whose hash is null (D3).

Each index carries its name both as the model name and as the database name, so a naming convention does not
rename it. These are the definitions HiveWeaver's migration `IndexEventStoreLookups` uses. Both are declared with
the precedence of a data annotation. EF Core drops a convention-sourced index that another index over the same
columns covers, and with two indexes on the sequence number, the first green run kept only the one declared last.

*Alternatives rejected:*
- Declaring the unique index in `EventStreamEntryConfiguration` unconditionally. A host that keeps the framework's
  key would get a second unique index over the primary-key column, which costs a write on every append and buys
  nothing.
- Declaring it in `WriteDbContext.OnModelCreating`. That code runs before the consumer's convention call, so it
  cannot know the final key.
- Changing `ApplyGlobalEntityConfiguration()` to leave the event stream's key alone. Both consumers' tables would
  move their primary key from `id` to `sequence_number` on a populated table, which is a heavy, blocking migration
  that drops `id`'s uniqueness. The convention is also public, and other consumers rely on it re-keying every
  entity.
- Making `EventStreamEntry` stop implementing `IEntity`. That is a breaking change to a public type.
- Registering the convention through an options extension in `AddNpgsqlWriteDbContextFactory`. A design-time
  factory does not go through that registration, so the migration would miss the indexes the runtime model has,
  and every start would report pending model changes.

*Evidence:* the implementation of `ApplyGlobalEntityConfiguration()` and the order of the calls in the consumers'
write contexts. Neither consumer overrides `ConfigureConventions`.

### D3 — The partial index's filter is written from the model's own column name

The filter is raw SQL, so it cannot be a constant. A host that maps the hash to `Hash` would get a filter that names
a column that does not exist. The convention reads the column name the model maps `Hash` to in the event table, and
delimits it with the provider's `ISqlGenerationHelper`, which the convention factory takes from the context's
service provider. It then writes `<column> IS NULL`. On Npgsql, an identifier that needs no quoting stays bare, so
the snake_case model yields `hash IS NULL`, exactly HiveWeaver's filter. The PascalCase model yields
`"Hash" IS NULL`.

The convention is added through `ConfigureConventions` and so runs after the naming convention's own finalizing
step, which means the column names are already final when it reads them. Where the provider is not relational (the
in-memory provider in unit tests), no SQL generation helper exists and the partial index is not declared.

*Evidence:* to be proven by tasks 1.3 and 1.4, which assert the filter text under both naming conventions and
apply the migration on PostgreSQL.

### D4 — The anchors get a plain index on the sequence number; the chaining reads stay as they are

`EventChainAnchorConfiguration` declares a non-unique index on `SequenceNumber`, named
`ix_event_chain_anchor_sequence_number`. It is not unique because anchors are unique per partition and sequence
only. The anchors are keyed by `Id` under either key convention, so this index is unconditional.

None of the chaining reads changes. With the indexes of D2 and this one, they are served as follows:
- `GetUnhashedEventsAsync` by the partial index, in sequence order, stopping at the batch size.
- `GetPreviousEventAsync`, `GetLastHashedEventAsync`, `GetManyAfterSequenceAsync` and `GetMaxSequenceNumberAsync` by
  the sequence-number index, or by the key where that is the sequence number. The last-hashed read scans backwards
  past the few unchained entries at the head.
- `GetLastSequenceNumberOrDefaultAsync([])` by a backward scan of the anchors' index.

*Alternative rejected:* reading the latest anchor per partition. That changes what the anchor interval measures,
which the *tamper-evident-streams* specification states as a global sequence distance.

### D5 — The framework's model does not create indexes concurrently

`CREATE INDEX` on a populated table blocks writes for as long as it runs. Npgsql can mark an index as created
concurrently, but such a migration cannot run in a transaction, and it would put a provider-specific annotation
into the shared model. The schema section of `docs/getting-started/di-composition.md` shows instead how a host with a large store edits its generated
migration: `CREATE INDEX CONCURRENTLY` in a migration that suppresses its transaction. Indexes created that way
match the model, so later migrations are empty.

## Risks / Trade-offs

- [A consumer overrides `ConfigureConventions` without calling `base`.] → Then its model lacks the sequence-number
  indexes, and nothing fails. The XML documentation of `WriteDbContext` and the schema section of
  `di-composition.md` say to call `base`, as they already say for `OnModelCreating`. The model test of task 1.2
  shows the effect.
- [A consumer declared the indexes itself under other names or definitions.] → Its migration replaces its index
  with the framework's. The schema section of `di-composition.md` states the names and definitions so the consumer can rename its own
  instead.
- [EF Core 9 and later throw on `Migrate()` when the model has pending changes.] → A host that upgrades without
  generating a migration fails at start-up, as with every schema addition before. The CHANGELOG puts the migration
  first, as 4.4.0 did.
- [The partial index costs a write on every append and on every chaining update.] → It holds only the entries not
  yet chained, which is the backlog of a few seconds, so it stays a few pages.
- [The filter depends on the naming convention having run first.] → Task 1.3 pins the filter text under both naming
  conventions.
- [A row a host inserted itself under a partition other than its stream id's is no longer found by the reads of its
  stream.] → The framework never writes one: both writers derive the partition from the stream id. Two of the
  framework's own test fixtures did, though (`SnapshotOwnerTests`, `TenantFilteredStoreReadTests`), and were
  corrected. The CHANGELOG states the requirement for a host's fixtures and imports.

## Migration Plan

1. Upgrade the packages and generate an EF Core migration for the write context. It adds up to three indexes and
   changes no table or column.
2. A host that bridged the indexes under the documented names and definitions removes its own declarations first.
   Its migration is then empty except for any other index it chooses to drop, such as a `(stream_id, version)`
   bridge, which the framework no longer needs.
3. A host with a large store can rewrite the generated `CreateIndex` calls as `CREATE INDEX CONCURRENTLY` with the
   transaction suppressed (D5).
4. Rollback: the indexes can be dropped. The reads of D1 stay correct without them, and on an older version of the
   framework the indexes are unused but harmless.
