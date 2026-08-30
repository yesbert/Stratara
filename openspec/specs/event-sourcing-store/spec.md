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
the former and must not retry the latter.

#### Scenario: Two writers race on one stream

- **WHEN** a save fails because another writer has already written the versions being appended
- **THEN** the failure identifies itself as a concurrency conflict, names the stream and the
  aggregate type, and the staged batch is cleared so a retry starts from a re-read

#### Scenario: A save fails for an unrelated reason

- **WHEN** a save fails for a reason that is not a concurrency conflict
- **THEN** the failure propagates unchanged and is not presented as a conflict

#### Scenario: An operator watches for contention

- **WHEN** conflicts occur
- **THEN** each is counted, dimensioned by the aggregate type and the partition it fell in

### Requirement: A successful save publishes what was written

A save that persists events SHALL publish them onward as one bundle carrying the session that
produced them, so that read models and process managers see the batch as it was committed.

#### Scenario: A batch is saved

- **WHEN** a save persists a batch of events
- **THEN** a bundle covering exactly those events is handed to the outbox, carrying the session
  context under which they were written

#### Scenario: No session is set

- **WHEN** a save is attempted with no session context
- **THEN** it fails rather than publishing a bundle with no attributable origin

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
caller for that event; the subject already established for that stream in the current batch; for a
tenant-scoped aggregate, the tenant recorded on the stream's first existing event; a tenant carried
by the event itself where the event declares itself a creation event; and only then the session's
data-owner tenant. Every candidate SHALL name a tenant to be used, including the explicitly supplied
one. Where none of these yields a tenant, the append SHALL fail rather than guess.

An explicit subject that names no tenant SHALL fail the append rather than fall through to the
remaining candidates, because a caller who stated the subject has already said which other candidate
is not to be used.

Reading the tenant from the stream before the session is what stops a privileged operator's session
silently re-homing an existing aggregate into another tenant.

#### Scenario: An existing tenant-scoped stream is appended to

- **WHEN** an event is appended to an existing stream of a tenant-scoped aggregate
- **THEN** the tenant recorded on that stream is used, even if the session names a different one

#### Scenario: A new tenant-scoped aggregate is created

- **WHEN** the first event of a tenant-scoped aggregate declares itself a creation event carrying a
  tenant
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

#### Scenario: A consumer migrates the store

- **WHEN** a consumer generates a migration from the framework's model
- **THEN** the event stream carries a unique constraint over partition, stream and version, so
  version collision is refused by the database and not only by the application
- **AND** snapshots carry the same uniqueness, and the integrity anchors carry a unique constraint
  over partition and sequence

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
