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
- **Neither layer checks a stream's owner, and the documentation says so.** The entrance guard compares the tenant a
  request names with the session's. It never sees which stream a command's aggregate id names. The framework takes a
  stream's owner from the stream, by design: a privileged session appends to another tenant's stream and the event
  is recorded for the stream's owner. On an unfiltered write context, the usual case, a command naming another
  tenant's aggregate id has always loaded and appended to that stream. The filter on the write context made such a
  command fail closed, as an empty load and then a version conflict, and the review showed it with a probe. It did
  so at the price of the failures above, and it did not do it for a stream with two owners. The owner confirmed on
  2026-09-27, with this stated, that the framework reads unfiltered. The guide now says that a handler which must
  refuse another tenant's stream checks the loaded aggregate's owner. A framework-side owner check would conflict
  with the owner-resolution rule for privileged sessions and was not chosen.
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

- [A consumer relied on the write context's filter to keep a tenant from another tenant's stream] → That protection
  existed for a stream with one owner and goes away. The release notes state it under *Changed*, and the guide tells
  a handler to check the loaded aggregate's owner. The alternative, keeping the filter, silently broke saga timers
  and two-owner streams.
- [A consumer's view truncator runs on a filtered read context] → The framework calls it without a session, so a
  truncator that does not ignore the filter deletes nothing, and the replay applies on top of the old rows. This is
  outside this change's code; the guide now says it.
