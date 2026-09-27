## Context

`ApplyGlobalTenantQueryFilters<TContext>(this)` installs `e => EF.Property<Guid>(e, "TenantId") == context.TenantId`
on every `IMultiTenant` entity type. `EventStreamEntry`, `Snapshot`, `EventChainAnchor` and `CommandLogEntry` are
`IMultiTenant`. EF composes global filters over LINQ and over `FromSqlRaw` alike. Raw statements executed through
`ExecuteSqlRaw`, `SqlQueryRaw` or a `DbCommand` are not filtered. That is why the native backfill, PostgreSQL's
positioning statement and the native reader's head were never affected.

## Goals / Non-Goals

**Goals:** a write context that declares the tenant filter behaves, for everything the framework does with its
store, exactly like one that does not.

**Non-Goals:** changing what the filter does for the consumer's own queries, or for the consumer's read contexts,
which is what the guide's helper is for.

## Decisions

- **The framework reads its store past the filters everywhere, not only across the store.** The first cut kept the
  reads about one stream filtered, on the assumption that they run under the owner's session. The independent
  review showed the assumption false twice (probe on SQLite).
  - `SagaProcessGrain.IsAliveAsync`, `OnTimeoutAsync` and the old `HandleAsync` overload read a process's state
    stream without a session. The filter hid a live process, and its timers were unregistered.
  - A stream may hold events of two owners (`AppendOnBehalfOfAsync`). Under one owner's session the filtered reads
    returned half the stream. The snapshot saved from it was wrong for good, and the owner's next append collided
    with the hidden version.

  Both follow from the same fact: the event store is one history, not a set of per-tenant tables. The owner decided
  on 2026-09-27 that every framework read of it ignores the filters.
  - *Sites:* the grep of `Set<EventStreamEntry|Snapshot|EventChainAnchor|CommandLogEntry>` across `src/` covers every
    read in `EventStreamRepository`, `SnapshotRepository`, `EventChainRepository`, `PortableCounterReader`,
    `PortableCounterStartupCheck` and `PostgresTransactionIdReader`. The portable backfill was covered by
    `read-and-prepare-a-long-history-in-linear-time`. `CommandLogEntry` is only written. Writes through
    `SaveChanges` are not filtered.
  - `IgnoreQueryFilters()` switches off every filter declared on the entity, not only the tenant filter. That is
    intended: every one of these reads needs every row, or a version, a position or the chain gets a gap. The
    contracts and the guide say "every query filter".
- **Isolation stays at the entrance.** A cross-tenant request is refused by the tenant-isolation guard before it
  reaches the store. The filter on the write context added nothing there that worked: a stream it hid reappeared as
  a version conflict on the next append.
- **The repository contracts say it once, at type level.** `IEventStreamRepository`, `ISnapshotRepository` and
  `IEventChainRepository` document that every read returns every tenant's rows. For a consumer calling them directly
  through a filtered write context, this is a behaviour change, recorded under *Changed* in the release notes.
- **Tests with a filtered context per surface.** A context applies the framework's own helper. Its ambient tenant is
  either empty (no session) or one of two tenants. The tests cover:
  - SQLite: every repository read, the portable reader and its start check, and one stream with two owners worked
    on through the event source (existence without a session, rebuild, snapshot, append).
  - PostgreSQL: the native reader.

  Each fails with the reads going through the filter (counter-check).

## Risks / Trade-offs

- [A consumer relied on the write context's filter to keep a tenant from another tenant's stream] → It never did
  that reliably. The hidden stream turned into a version conflict, and the framework's own work broke with it. The
  entrance guard is where that protection lives, and the guide now says so.
