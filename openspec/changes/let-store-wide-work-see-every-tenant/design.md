## Context

`ApplyGlobalTenantQueryFilters<TContext>(this)` installs `e => EF.Property<Guid>(e, "TenantId") == context.TenantId`
on every `IMultiTenant` entity type. `EventStreamEntry`, `Snapshot`, `EventChainAnchor` and `CommandLogEntry` are
`IMultiTenant`. EF composes global filters over LINQ and over `FromSqlRaw` alike. Raw statements executed through
`ExecuteSqlRaw` or a `DbCommand` are not filtered. That is why the native backfill and PostgreSQL's positioning
statement were never affected.

## Goals / Non-Goals

**Goals:** every framework read that serves all tenants ignores the filters, and every read about one stream keeps
them.

**Non-Goals:** changing what the filter does for consumer queries or for per-stream reads under a session. Owner
resolution reads the stream's first entry under the session. Under a filtered context and a session of another
tenant it sees no stream, and the append then conflicts on the version index rather than re-homing anything. That
is the filter doing its job.

## Decisions

- **`IgnoreQueryFilters()` on the store-wide reads only.** The list is complete for the framework's entity reads: a
  grep of `Set<EventStreamEntry|Snapshot|EventChainAnchor|CommandLogEntry>` across `src/`.
  - `EventStreamRepository`: `GetUnhashedEventsAsync`, `GetPreviousEventAsync`, `GetLastHashedEventAsync`,
    `GetManyAfterSequenceAsync`, the stream-order read's range extension and straggler search, and
    `GetMaxSequenceNumberAsync`.
  - `EventChainRepository.GetLastSequenceNumberOrDefaultAsync`.
  - `PortableCounterReader`: read, head and blocking entry.
  - `PortableCounterStartupCheck`.
  - `PostgresTransactionIdReader`: both `FromSqlRaw` reads.

  `CommandLogEntry` is only written. `Snapshot` is only read per stream.
- **Document on the contract, not only in the implementation.** The store-wide members of `IEventStreamRepository`
  and `IEventChainRepository` say in their remarks that they return every tenant's entries whatever query filters the
  context declares. An implementation a consumer writes then knows the contract.
- **Tests with a filtered context per surface.** The tests use a context that applies the framework's own helper with
  no session, so every filter matches the empty tenant. The entries belong to several real tenants. Each test fails
  with the reads going through the filter (counter-check).

## Risks / Trade-offs

- [A consumer relied on the filter to keep a reader, a replay or the hash chain to one tenant] → That never worked. A
  store-wide worker has no session, so the filter matched no tenant at all rather than one. Recorded under *Fixed*.
