# event-sourcing-store Specification

## Purpose
Record what happened as an ordered, append-only sequence of facts per aggregate, so that current
state is always derivable, history is never overwritten, and two writers racing on one aggregate
cannot both win.

## Requirements

### Requirement: An event stream is created once and appended to thereafter

Creating a stream SHALL fail if that stream already exists, and appending SHALL be the only way to
add to an existing one. There SHALL be no operation that rewrites or removes a recorded event.

#### Scenario: A stream is created

- **WHEN** a stream is created with its first event
- **THEN** that event is recorded at version 1

#### Scenario: A stream is created twice

- **WHEN** creation is attempted for a stream that already exists
- **THEN** it fails, with a message naming the stream and directing the caller to append instead

#### Scenario: Events are appended to an existing stream

- **WHEN** events are appended to a stream whose current version is not known to the caller
- **THEN** the framework determines the current version itself and continues numbering from it

### Requirement: Versions are consecutive and assigned per stream

Each event appended to a stream SHALL receive the next consecutive version within that stream,
starting at 1. Versions SHALL be unique per stream.

#### Scenario: Several events are appended together

- **WHEN** several events are appended to one stream in one operation
- **THEN** they receive consecutive versions in the order they were appended

#### Scenario: Two streams are appended to

- **WHEN** events are appended to two different streams
- **THEN** each stream's versions are numbered independently

### Requirement: Appends are buffered and become durable on an explicit save

Appending SHALL stage an event rather than persist it. Persistence SHALL happen when the caller
explicitly saves, and SHALL cover every event staged since the last save as one unit.

A caller therefore controls the transaction boundary, and a failure discards the whole staged
batch — not the individual event that conflicted.

#### Scenario: Several events are staged and saved

- **WHEN** events are appended across several calls and then saved once
- **THEN** all of them are persisted together

#### Scenario: A save succeeds

- **WHEN** a save completes
- **THEN** the staged batch is cleared, so a subsequent save does not re-persist it

### Requirement: A concurrency conflict discards the batch and is distinguishable

Where a save conflicts with a concurrent writer on any stream in the batch, the framework SHALL
signal a concurrency conflict identifying the stream and aggregate type, SHALL clear the staged
batch, and SHALL record the conflict as a measurement dimensioned by aggregate type.

A conflict SHALL be distinguishable from any other persistence failure, so that a caller can retry
the former and must not retry the latter — and it SHALL be distinguishable on every database
provider the framework ships a store registration for, not only on the one it was first built on.
A version collision that the database refuses through its uniqueness constraint is a concurrency
conflict on every provider.

#### Scenario: Two writers race on one stream

- **WHEN** a save fails because another writer has already written the versions being appended
- **THEN** the failure identifies itself as a concurrency conflict, names the stream and the
  aggregate type, and the staged batch is cleared so a retry starts from a re-read

#### Scenario: Two writers race on a provider other than PostgreSQL

- **WHEN** a save fails because of a version collision on a provider the framework ships a
  registration for
- **THEN** the failure is the same concurrency conflict a PostgreSQL store signals — verified on the
  SQLite store the test-support package registers

#### Scenario: A save fails for an unrelated reason

- **WHEN** a save fails for a reason that is not a concurrency conflict
- **THEN** the failure propagates unchanged and is not presented as a conflict

#### Scenario: An operator watches for contention

- **WHEN** conflicts occur
- **THEN** each is counted, dimensioned by the aggregate type and the partition it fell in

### Requirement: A successful save publishes what was written

A save that persists events SHALL publish them onward as one bundle carrying the session that
produced them, so that read models and process managers see the batch as it was committed.

On a host that has opted in to durable bundles (`outbox-and-messaging` → *Dispatch attempts the bus
first and falls back to durable storage*), the bundle SHALL be written to durable storage in the
transaction that commits the events, so that a save either persists both or neither, and the save
SHALL NOT fail after the commit because the bundle could not be recorded.

#### Scenario: A batch is saved

- **WHEN** a save persists a batch of events
- **THEN** a bundle covering exactly those events is handed to the outbox, carrying the session
  context under which they were written

#### Scenario: A batch is saved with durable bundles

- **WHEN** a save persists a batch of events on a host with durable bundles
- **THEN** the bundle is durable in the same commit as the events, and a failure to record it fails
  the save before anything is committed — the atomic commit verified on the PostgreSQL store, the
  refused record on the SQLite store the test-support package registers

#### Scenario: No session is set

- **WHEN** a save is attempted with no session context
- **THEN** it fails rather than publishing a bundle with no attributable origin, with a failure that
  identifies itself as a missing identity and can be caught without reference to the store

### Requirement: Every recorded event carries its provenance

Each recorded event SHALL carry, alongside its payload: the stream and version it belongs to, its
event and aggregate type names, when it was recorded, the correlation and causation identities of
the operation that produced it, the actor who triggered it, and the tenant and user who own it.

#### Scenario: An event is recorded

- **WHEN** an event is appended
- **THEN** it carries all of the above, so that a later reader can attribute it without consulting
  anything else

#### Scenario: An event is counted

- **WHEN** an event is appended
- **THEN** it is counted, dimensioned by its event type and its aggregate type

### Requirement: The owning tenant is resolved from the stream before the session

The tenant an event belongs to SHALL be resolved in this order: an explicit subject supplied by the
caller for that event; the subject already established for that stream in the current batch; the
tenant recorded on the stream's first existing event; a tenant carried by the event itself where the
event declares itself a creation event; and only then the session's data-owner tenant. Every
candidate SHALL name a tenant to be used, including the explicitly supplied one. Where none of these
yields a tenant, the append SHALL fail rather than guess.

An explicit subject that names no tenant SHALL fail the append rather than fall through to the
remaining candidates, because a caller who stated the subject has already said which other candidate
is not to be used.

Reading the tenant from the stream before the session is what stops a privileged operator's session
silently re-homing an existing aggregate into another tenant. That reasoning does not depend on the
aggregate's shape, so neither does the rule: **a stream's recorded owner is stable for every
aggregate**, whether or not the aggregate exposes its tenant as a property.

An aggregate whose events carry different owners cannot be fully erased — each tenant's erasure
reaches only its own entries — and, once one of those keys is shredded, cannot be rehydrated at all,
because the remaining entries are decrypted under a key that no longer exists. A consumer that
genuinely wants an event attributed to another subject SHALL state it explicitly rather than obtain
it by omission.

#### Scenario: An existing tenant-scoped stream is appended to

- **WHEN** an event is appended to an existing stream
- **THEN** the tenant recorded on that stream is used, even if the session names a different one,
  and regardless of whether the aggregate exposes a tenant of its own

#### Scenario: A new tenant-scoped aggregate is created

- **WHEN** the first event of an aggregate declares itself a creation event carrying a tenant
- **THEN** that tenant is used

#### Scenario: The caller supplies the subject explicitly

- **WHEN** the caller appends on behalf of a stated subject
- **THEN** that subject is used for that event, overriding every other source, and the override
  applies to that event only

#### Scenario: The caller supplies a subject that names no tenant

- **WHEN** the caller appends on behalf of a subject whose tenant is absent
- **THEN** the append fails with a message naming the event and the stream, no event is recorded,
  and the remaining candidates are not consulted

#### Scenario: Nothing identifies a tenant

- **WHEN** no explicit subject, no stream history, no creation event and no session tenant is
  available
- **THEN** the append fails with a message naming the event, the stream, and the three ways to
  supply a subject

### Requirement: Events are persisted with their payload protected

An event's payload SHALL be serialized through the framework's protecting serializer, scoped to the
resolved owning tenant and user, so that fields marked for encryption are ciphertext at rest and on
the wire.

#### Scenario: An event carries a protected field

- **WHEN** an event with a field marked for encryption is appended
- **THEN** the persisted payload holds ciphertext for that field, scoped to the resolved owner

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

### Requirement: Events are immutable single facts and persisted values are never removed

An event SHALL be an immutable record of one business fact. A value that has been persisted as part
of an event — an enumeration member in particular — SHALL NOT be removed from the code, because
data already written would become unreadable.

#### Scenario: An enumeration member becomes obsolete

- **WHEN** a value that has been written to the store is no longer used
- **THEN** it is marked obsolete and retained, rather than deleted

#### Scenario: A field changes on an update

- **WHEN** an update changes several fields of an aggregate
- **THEN** one event is recorded per changed field rather than one event carrying the new whole

### Requirement: Registering the store's context makes the store usable

Registering the write store's database context through the framework's registration SHALL make the
write-side unit of work available to everything that depends on it — appending events, publishing
through the outbox, handling commands in a worker — without a further registration by the consumer.
A write-side unit of work the consumer registers itself SHALL take precedence over the framework's.

The same registration SHALL make the store recognise its provider's version collision as a
concurrency conflict. A consumer that brings a provider the framework has no registration for MAY
register its own recognition of that provider's collision, and the framework SHALL consult it
alongside its own.

A store whose context is registered but whose unit of work is not is a store that fails at the first
command with an error naming a type no guide mentions. The registration that declares the context
is the one place that knows which context the unit of work should be built over — and which
provider's error means "someone else wrote first".

#### Scenario: A consumer registers only the write context

- **WHEN** a consumer registers its write-store context through the framework's registration and
  nothing else
- **THEN** the write-side unit of work resolves and is bound to that context, and a command that
  appends an event can be handled

#### Scenario: A consumer supplies its own write-side unit of work

- **WHEN** a consumer registers its own write-side unit of work, before or after registering the
  context
- **THEN** the consumer's unit of work is the one resolved

#### Scenario: The registration is applied more than once

- **WHEN** the write-store context is registered more than once for the same context type
- **THEN** one unit of work is resolved, bound to that context

#### Scenario: A consumer brings a provider the framework does not ship

- **WHEN** a consumer registers its own recognition of a provider's version collision
- **THEN** a collision that recognition identifies is signalled as a concurrency conflict, and the
  framework's own recognitions keep working beside it

### Requirement: The store can be read in commit order without skipping a late committer

The framework SHALL offer a reader that returns a partition's entries after a position, in an order
in which no entry at or below the position of a returned batch can still commit later, so that a
consumer resuming from a stored position never skips an entry. A batch SHALL say whether more
entries exist, and a batch SHALL never end in the middle of one transaction's entries: a transaction
whose entries would straddle the end of a batch SHALL be held back whole for the next batch, and a
transaction that alone holds more entries than a batch SHALL be returned whole, so that a batch MAY be
larger than the size asked for. A reader SHALL
report a partition's head: the position after which no entry committed at the time of the call exists,
so that a consumer can start there. A head SHALL be taken while nothing appends — the deployment's own
seeding is where a head is taken — because a reader whose positions come from the store's transactions
cannot count what a transaction still open might yet commit before what it already sees: taken while
writers run, its head is held back, and a consumer seeded at it applies what those writers commit a
second time rather than skipping it. The documentation SHALL say so where it tells a deployment to
seed. Two readers
SHALL be offered: one native to PostgreSQL that adds no work to an append, and one for any relational
provider the framework ships a store registration for, under which concurrent appends to one partition
wait for each other. A store that holds entries from before the native reader's commit record was
added SHALL be adoptable without rewriting its event table, and the backfill that does it SHALL walk the
history once rather than once per batch, so that a store whose history is long is adoptable at all: the
documented migration adds the record
without a rewrite, and the framework's backfill stamps the existing entries in the order they were
appended, in bounded batches each committed on its own, so that the reader returns history in bounded
batches and in append order; the backfill SHALL run while nothing appends, and the documentation SHALL
say so. The portable reader SHALL see only entries appended by a process that maintains
its partition counter; every process that appends to a store read by it SHALL maintain the counter, and
a reader that finds an entry appended without it SHALL stop its partition and report the entry rather
than read past it — whatever the entry's place in its partition and however many entries without a
position other partitions hold. Maintaining the counter is the write context's to declare through the
documented interceptor; the framework SHALL NOT offer a setting that appears to switch the counter on
or off without doing so, and the setting that once did SHALL be marked obsolete with a message naming
the interceptor until it is removed. Positions handed out to one append SHALL follow the version order
within each stream. Positioning an entry that was appended without the counter SHALL NOT change a
position already handed out: the entry SHALL take a position after every position the partition has
handed out, so that a checkpoint written before the positioning stays true and a reader resumes from
it, reads the positioned entry once, and re-applies nothing. The documentation SHALL state that such an
entry is therefore read after entries appended with the counter in the meantime — a later version of
its own stream included — that the process appending without the counter is to be stopped before
positioning, and that a read model which stops on the resulting order is repaired by a rebuild.
The partition count of a store read by the portable reader SHALL NOT change without
renumbering its entries, and a host whose partition count is lower than the store's counter rows show
SHALL refuse to start. A position SHALL NOT be accepted by the other reader, nor under a partition count
other than the one it was written under. A store that holds entries from before the portable reader
was adopted SHALL be positioned once before that reader serves, and the reader SHALL refuse to start on
a store with entries it has not positioned.

#### Scenario: A long transaction commits after a later one

- **WHEN** one transaction holds an append open while another appends and commits, and a reader
  reads in between
- **THEN** the entry of the long transaction is returned by a later read and never skipped — verified
  in two hundred randomised interleavings per reader on the PostgreSQL store

#### Scenario: One transaction holds more entries than a batch

- **WHEN** one transaction appends more entries to a partition than the batch size a consumer reads
  with, and other transactions append before and after it
- **THEN** a batch that reaches that transaction returns it whole and larger than the batch size, a
  transaction that would straddle the end of a batch is held back whole for the next one, and
  transactions that fit are cut at the batch size — verified for the native reader on the PostgreSQL
  store

#### Scenario: A reader reports its head

- **WHEN** a consumer asks a reader for a partition's head while nothing appends, and then reads
  after that position
- **THEN** no entry committed before the head was asked for is returned, and an entry committed
  afterwards is — verified for both readers on the PostgreSQL store

#### Scenario: A head is taken while a writer's transaction is open

- **WHEN** a head is asked for while a write transaction of the store is still open
- **THEN** it names no position an entry of that transaction could yet precede, so a consumer seeded
  at it applies what that transaction commits rather than skipping it

#### Scenario: A populated PostgreSQL store adopts the native reader

- **WHEN** a store holding entries written before the commit record existed is migrated as documented
  and backfilled while nothing appends
- **THEN** the event table is not rewritten by the migration, a reader returns the history in batches
  no larger than the backfill's batch and in the order the entries were appended, and a batch never
  holds more than one backfill batch's entries — verified on the PostgreSQL store

#### Scenario: A consumer switches readers

- **WHEN** a checkpoint written under one reader is read under the other
- **THEN** the read is refused with a message naming both readers, rather than misread

#### Scenario: A consumer changes the partition count

- **WHEN** a checkpoint written under one partition count is read after the host changed the count
- **THEN** the read is refused with a message naming both counts, rather than skipping entries

#### Scenario: A store with history adopts the portable reader

- **WHEN** a store holding entries written before the upgrade adopts the portable reader
- **THEN** the documented backfill positions those entries in commit order within each partition, and
  a reader started before the backfill refuses to start with a message that names it

#### Scenario: A process appends without maintaining the counter

- **WHEN** a store is read by the portable reader and a process that does not maintain the partition
  counter appends an entry
- **THEN** the reader of that entry's partition stops before it, reports the unpositioned entry and
  what is missing, and resumes once the entry is positioned — verified on the PostgreSQL store only

#### Scenario: The partition count is lowered under the portable reader

- **WHEN** a host that reads with the portable reader starts with a partition count lower than the
  number of counter rows the store holds
- **THEN** it refuses to start with a message naming both counts, instead of reading merged partitions
  whose positions overlap — verified on the PostgreSQL store only

#### Scenario: A late entry is positioned behind a checkpoint

- **WHEN** a reader holds a checkpoint past several positioned entries, an entry of its partition is
  appended without the counter, and the entry is then positioned
- **THEN** the positioned entries keep their positions, the reader resumes from its checkpoint, reads
  the late entry once after them, and re-applies none of them — verified on the PostgreSQL store only

#### Scenario: Unpositioned entries pile up in other partitions

- **WHEN** many entries are appended without the counter to other partitions after one was appended
  without it to the reader's own
- **THEN** the reader of that partition still stops before its own unpositioned entry, however many the
  others hold — verified on the PostgreSQL store only with more foreign entries than one read of the
  store returns

#### Scenario: One append holds several versions of one stream

- **WHEN** one save appends several versions of one stream, and of another stream, to a store that
  maintains the counter
- **THEN** the positions handed out follow the version order within each stream, and a reader returns
  them in that order — verified on the PostgreSQL store only

#### Scenario: A host sets the retired switch

- **WHEN** a host sets the setting that once claimed to maintain the counter
- **THEN** the build warns that it is obsolete, names the interceptor as what maintains the counter, and
  the value changes nothing
