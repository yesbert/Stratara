## MODIFIED Requirements

### Requirement: Tenant-scoped rows are filtered at the database as well as at the entrance

The framework SHALL additionally constrain every entity declared tenant-scoped to the ambient
tenant of its database context, so that a query the consumer's code makes, reaching the store without
passing the request guard, still cannot return another tenant's rows.

The two layers are independent on purpose: the entrance guard covers requests, and the query filter
covers every query the consumer's code issues through the context, including ones the guard never saw.

The framework's own event store is not such a query. Its events, snapshots and hash-chain anchors
are one history: a stream's version and owner are decided across all its entries, a stream may hold
events of more than one owner, and the framework reads the store without a session on behalf of
every tenant — in commit order and at start, for a replay, for the hash chain, to prepare its
history, and for work on one stream such as a process's timeout. The framework's reads of its store
SHALL therefore see every entry whatever query filters the consumer's write context declares, so
that declaring the filter there never silently empties a reader, a replay or the hash chain, drops
a timer, or leaves a stream half read. A filter declared on the write context SHALL apply to the
consumer's own queries of those entities only, exactly as if the write context declared none.

Neither layer checks the owner of a stream against the session: the guard compares the tenant a
request names with the session's, and the framework takes a stream's owner from the stream. A command
that names another tenant's stream therefore loads it and appends to it, recording the event for the
stream's owner with the session's actor, whether or not the write context declares the filter. The
documentation SHALL say so, and SHALL say that a handler which must refuse another tenant's stream
checks the owner of the aggregate it loaded.

#### Scenario: A tenant-scoped entity is queried

- **WHEN** an entity type declared tenant-scoped is queried through a tenant-scoped context
- **THEN** only rows whose tenant matches that context's ambient tenant are returned

#### Scenario: An entity type is not declared tenant-scoped

- **WHEN** an entity type does not declare itself tenant-scoped
- **THEN** no filter is installed for it — the framework filters what a consumer marks, and marking
  is the consumer's decision

#### Scenario: The write context declares the tenant filter

- **WHEN** a consumer's write context declares the tenant query filter and holds the entries of
  several tenants
- **THEN** the commit-order readers return every tenant's entries, the portable reader's start check
  refuses an unpositioned entry of any tenant, and the reads a replay and the hash chain make return
  every tenant's entries and anchors — verified on SQLite for the portable reader, its start check
  and the reads of the replay and the hash chain, and on PostgreSQL for the native reader

#### Scenario: One stream is worked on through a filtered write context

- **WHEN** the framework works on one stream through a write context that declares the tenant query
  filter — without a session, or under the session of one of the stream's owners, for a stream
  holding events of two owners
- **THEN** it finds the stream, rebuilds it from every entry and snapshot, snapshots it in full, and
  lets the stream's owner append to it at its true version — verified on SQLite
