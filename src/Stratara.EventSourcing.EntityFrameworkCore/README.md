# Stratara.EventSourcing.EntityFrameworkCore

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

> **License:** [MIT](../../LICENSE).

EF Core persistence for the Stratara event-sourced stack — PostgreSQL flavoured via Npgsql + pgvector. Bundles four previously-separate Stratara projects into one NuGet because they always ship together:

| Folder | Contents | Old csproj |
|---|---|---|
| `EntityFrameworkCore/` | shared EF conventions, value generators, `IDbContext` / `IReadDbContext` / `IWriteDbContext` / `ITenantScopedDbContext` / `IIdentityDbContext`, `UnitOfWork` base, `DefaultDbResolver`, `NpgsqlDbContextServiceCollectionExtensions`, `DbContextMigrationUtility` | `Stratara.EntityFrameworkCore` |
| `WriteStore/` | `WriteDbContext`, `WriteUnitOfWork`, event-stream/snapshot/event-chain/command-audit/outbox repositories + entity configurations | `Stratara.EventSourcing.EntityFrameworkCore.WriteStore` |
| `ReadStore/` | `ReadDbContext`, `ReadUnitOfWork`, `ProjectionsUnitOfWork`, Tenant repository, projection entity configurations | `Stratara.EventSourcing.EntityFrameworkCore.ReadStore` |
| `IdentityStore/` | Generic ASP.NET Identity `IdentityDbContext` + marker | `Stratara.EventSourcing.EntityFrameworkCore.IdentityStore` |

Everything lives under `Stratara.EventSourcing.EntityFrameworkCore` and its sub-namespaces (`.WriteStore`, `.ReadStore`, `.IdentityStore`, `.Abstractions`, `.Conventions`, `.Extensions`, `.HealthChecks`, `.Migration`). The DI extensions sit in `Microsoft.Extensions.DependencyInjection`, per the Microsoft convention, so they resolve without an extra `using`.

## Why folded

WriteStore-without-ReadStore is not a real use case. EF conventions + value generators + UnitOfWork primitives are foundational for every other store. ASP.NET Identity glue follows the same EF Core conventions. Splitting into 4 NuGets that always ship together adds version-management noise (4× `<PackageVersion>` to keep in sync, transitive-resolution risk) without any consumer benefit.

If your application doesn't use ASP.NET Identity, simply don't reference `IdentityDbContext`-derived types — the rest of the package works without them.

## Quick start

Derive your contexts from the generic base classes — the type argument is the context itself:

```csharp
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;

public sealed class MyAppWriteDbContext(DbContextOptions<MyAppWriteDbContext> options)
    : WriteDbContext<MyAppWriteDbContext>(options);

public sealed class MyAppReadDbContext(DbContextOptions<MyAppReadDbContext> options)
    : ReadDbContext<MyAppReadDbContext>(options);
```

Then register the Npgsql context factories and the write store. Each factory registration also
registers the unit of work over its context — `IWriteUnitOfWork` for the write side,
`IProjectionsUnitOfWork` / `IReadUnitOfWork` for the read side — unless the host registered its own,
so this is all the store needs from the host:

```csharp
// In your AppHost / Worker / Web project:
builder.Services
    .AddNpgsqlWriteDbContextFactory<MyAppWriteDbContext>()
    .AddNpgsqlReadDbContextFactory<MyAppReadDbContext>()
    .AddWriteStore(builder.Configuration);                    // binds Stratara:EventSourcing options
```

Most hosts don't call these directly — the worker composites in `Stratara.EventSourcing.WorkerDefaults`
(`AddCommandWorkerServices()`, `AddEventProjectionWorkerServices()`, …) already compose `AddWriteStore`
for you. Reach for the explicit form when you build a host that none of the composites fit.

> **Renamed in 3.2.0, removed in 4.0.0:** the write-side factory was `AddNpsqlWriteDbContextFactory`
> (missing the `g`) through 3.1.x. `AddNpgsqlWriteDbContextFactory` replaced it in 3.2.0 and the old
> spelling stayed as an `[Obsolete]` alias until 4.0.0, which removed it. A host still on the old
> spelling adds the `g`; nothing else about the call changes.

## Health checks

Two opt-in readiness checks plug into any `IHealthChecksBuilder` (they require the write store above to be registered):

```csharp
builder.Services.AddHealthChecks()
    .AddEventStoreHealthCheck()                                  // write-side DB reachable?
    .AddOutboxHealthCheck(degradedThreshold: 1_000,             // outbox backlog depth
                          unhealthyThreshold: 10_000);
```

- `AddEventStoreHealthCheck()` — probes write-store connectivity; `Unhealthy` when the database cannot be reached.
- `AddOutboxHealthCheck(degradedThreshold?, unhealthyThreshold?)` — reports the pending outbox backlog under the `pending` data key and escalates to `Degraded` / `Unhealthy` when the count crosses the supplied thresholds (omit them to stay healthy while reachable).

Both are tagged `ready` by default, so they show up on a readiness endpoint rather than the liveness (`live`) one.

## Dependencies

- `Stratara.Projections` — for projection types used by `ProjectionsUnitOfWork`.
- `Stratara.Shared` — for diagnostics + abstractions + resilience.
- `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.Diagnostics.HealthChecks`, `Pgvector.EntityFrameworkCore`.
