# Stratara.EventSourcing.EntityFrameworkCore

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

EF Core persistence for the Stratara event-sourced stack — PostgreSQL flavoured via Npgsql + pgvector. Bundles four previously-separate Stratara projects into one NuGet because they always ship together:

| Folder | Contents | Old csproj |
|---|---|---|
| `EntityFrameworkCore/` | shared EF conventions, value generators, `IDbContext` / `IReadDbContext` / `IWriteDbContext` / `ITenantScopedDbContext` / `IIdentityDbContext`, `UnitOfWork` base, `DefaultDbResolver`, `NpgsqlDbContextServiceCollectionExtensions`, `DbContextMigrationUtility` | `Stratara.EntityFrameworkCore` |
| `WriteStore/` | `WriteDbContext`, `WriteUnitOfWork`, event-stream/snapshot/event-chain/command-audit/outbox repositories + entity configurations | `Stratara.EventSourcing.EntityFrameworkCore.WriteStore` |
| `ReadStore/` | `ReadDbContext`, `ReadUnitOfWork`, `ProjectionsUnitOfWork`, Tenant repository, projection entity configurations | `Stratara.EventSourcing.EntityFrameworkCore.ReadStore` |
| `IdentityStore/` | Generic ASP.NET Identity `IdentityDbContext` + marker | `Stratara.EventSourcing.EntityFrameworkCore.IdentityStore` |

Namespaces are unchanged from the pre-fold layout (`Stratara.EntityFrameworkCore`, `Stratara.EventSourcing.EntityFrameworkCore.WriteStore`, `Stratara.EventSourcing.EntityFrameworkCore.ReadStore`, `Stratara.EventSourcing.EntityFrameworkCore.IdentityStore`) so consumer `using` directives don't change.

## Why folded

WriteStore-without-ReadStore is not a real use case. EF conventions + value generators + UnitOfWork primitives are foundational for every other store. ASP.NET Identity glue follows the same EF Core conventions. Splitting into 4 NuGets that always ship together adds version-management noise (4× `<PackageVersion>` to keep in sync, transitive-resolution risk) without any consumer benefit.

If your application doesn't use ASP.NET Identity, simply don't reference `IdentityDbContext`-derived types — the rest of the package works without them.

## Quick start

```csharp
// In your AppHost / Worker / Web project:
builder.Services.AddNpgsqlWriteStore<MyAppWriteDbContext>(builder.Configuration);
builder.Services.AddNpgsqlReadStore<MyAppReadDbContext>(builder.Configuration);
```

Then derive `MyAppWriteDbContext : Stratara.EventSourcing.EntityFrameworkCore.WriteStore.WriteDbContext` and `MyAppReadDbContext : Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ReadDbContext`.

## Dependencies

- `Stratara.Projections` — for projection types used by `ProjectionsUnitOfWork`.
- `Stratara.Shared` — for diagnostics + abstractions + resilience.
- `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore`, `Pgvector.EntityFrameworkCore`.
