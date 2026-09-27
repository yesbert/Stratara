## MODIFIED Requirements

### Requirement: Tenant-scoped rows are filtered at the database as well as at the entrance

The framework SHALL additionally constrain every entity declared tenant-scoped to the ambient
tenant of its database context, so that a read reaching the store without passing the request
guard still cannot return another tenant's rows.

The two layers are independent on purpose: the entrance guard covers requests, and the query filter
covers every query the context issues, including ones the guard never saw.

The framework's own work across the store is not such a query: reading the store in commit order and
checking it at start, replaying it, chaining its hashes and preparing its history run without a
session, on behalf of every tenant. That work SHALL see every tenant's entries whatever query filters
the consumer's write context declares, so that declaring the filter there never silently empties a
reader, a replay or the hash chain. A query the framework makes about one stream — whether it exists,
its first entry, its version, its entries, its snapshots — SHALL keep the filters the context
declares.

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

#### Scenario: A stream is read through a filtered write context

- **WHEN** the framework reads one stream through a write context that declares the tenant query
  filter, under a session of another tenant
- **THEN** the stream's entries are not returned, as for any query through that context
