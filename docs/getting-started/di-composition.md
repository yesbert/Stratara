---
title: "DI Composition"
description: "Which Add*Services call a host needs, chosen by the shape of work the host does rather than by the packages it happens to reference."
---

# DI Composition

> **Derived page.** The behaviour described here is specified by the `host-composition` and
> `event-sourcing-store` capabilities under `openspec/specs/`. Those specifications are the source;
> this page explains and illustrates them. Where the two disagree, the specification is right and this
> page is a bug.

Stratara composes via à-la-carte `Add*Services()` extension methods on `IServiceCollection` and `IHostApplicationBuilder`. A typical host picks two or three, never all of them. Pick by **what shape of work** the host does, not by what packages it references.

## The decision tree

```
┌─ Are you a worker host or an HTTP host?
│
├─ HTTP host (ASP.NET Core minimal-API / MVC)
│  └─→ builder.AddBackendServices()                 (Mediator, Identity, Session, Security, Resilience)
│      app.MapDefaultEndpoints()                     (/health + /alive endpoints)
│
└─ Worker host (background service)
   ├─ Need to handle commands?         → builder.AddCommandWorkerServices()
   ├─ Need a dedicated lane for slow,  → builder.AddHeavyCommandWorkerServices(dop?)
   │  long-running commands?              (drains IHeavyCommand — see "Opt-in: heavy-command lane")
   ├─ Need to run projections?         → builder.AddEventProjectionWorkerServices()
   ├─ Need to run sagas?               → builder.AddSagaWorkerServices()
   ├─ On the Orleans execution model?  → builder.AddCommandServices() / AddEventProjectionServices() /
   │                                      AddSagaServices() — the same stacks without the bus-fed worker
   ├─ Need to hash event streams?      → builder.AddEventStreamHashWorkerServices()
   └─ Need to drain the outbox?        → builder.AddOutboxWorkerServices()
```

You'll typically run **one host per worker concern** in production — each `Add*WorkerServices()` boots the right `IHostedService`s and the supporting infrastructure.

## Shared umbrellas

`AddCommonFrameworkServices()` (called automatically by every worker + the backend variant) wires:

- `IMediator` + pipeline behaviors
- `IMessageBus` (RabbitMQ or Azure Service Bus, whichever you've referenced)
- Channel-agnostic identity primitives
- `SessionContextMiddleware` for ASP.NET (or the equivalent for non-HTTP channels)
- AES-GCM `[EncryptData]` infrastructure
- Polly named pipelines from `Stratara.Resilience`

Redis is not part of the base. The replay state every dispatching composite registers is held in
process unless `builder.AddCaching()` registers a Redis connection, in which case it is shared and a
replay spans hosts; the multi-replica outbox lock (`AddRedisOutboxLock()`) needs the same connection.

You almost never call `AddCommonFrameworkServices()` directly — it's a transitive dependency of the worker / backend extensions.

## Handler / projection / saga discovery

After wiring the workers, you still need to tell Stratara **which** handlers / projections / sagas to register. These are assembly scans:

```csharp
services
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>()
    .AddProjectionsFromAssemblyContaining<Program>()
    .AddSagasFromAssemblyContaining<Program>()
    .AddAggregatesFromAssemblyContaining<Program>();        // also registers ITrustedTypeResolver
```

A typical host calls `AddCommandHandlersFromAssemblyContaining<T>()` once per assembly that holds command handlers. Most apps have one host-level assembly + one domain assembly.

## Opt-in: request validation

Validation is **not** wired by the umbrellas — add it explicitly, **before** the handlers, so the behavior runs outermost (rejecting invalid requests before authorization, auditing, or the handler):

```csharp
services
    .AddStrataraValidation()                          // pipeline behavior — register first
    .AddValidatorsFromAssemblyContaining<Program>();  // discover every IValidator<T>
```

See **[Write a Validator](../guides/write-a-validator.md)**.

## Opt-in: tenant isolation

Also opt-in, registered **after** validation so it runs just inside it. The behavior guards requests that implement `ITenantScopedRequest`, rejecting any whose `TenantId` doesn't match the session's data-owner tenant — the command-/query-entrance complement to the database-side tenant query filters:

```csharp
services
    .AddStrataraValidation()
    .AddStrataraTenantIsolation();                    // guards ITenantScopedRequest

// Strict mode + a platform-admin cross-tenant escape:
services
    .AddStrataraTenantIsolation(o => o.Mode = TenantIsolationMode.Strict);
services.AddScoped<ICrossTenantAuthorizer, PlatformAdminCrossTenantAuthorizer>();
```

See **[Enforce Tenant Isolation](../guides/enforce-tenant-isolation.md)**.

## Opt-in: heavy-command lane

Long-running commands (bulk back-fills, crawls, re-indexing) can monopolise every command-worker slot and starve interactive commands. Mark such a command with `IHeavyCommand` and it is published to a separate topic that a dedicated heavy-command worker drains, so the interactive lane stays free:

```csharp
public sealed record ReindexCorpusCommand(Guid CorpusId) : ICommand, IHeavyCommand;
```

Run the heavy lane either alongside the interactive worker (two lanes, one host) or as a separately scaled host:

```csharp
// Two lanes in one host:
builder.AddCommandWorkerServices();
builder.Services.AddHeavyCommandWorker(degreeOfParallelism: 2);

// Or a dedicated, independently scaled heavy host:
builder.AddHeavyCommandWorkerServices(degreeOfParallelism: 2);
```

`degreeOfParallelism` bounds how many heavy commands run at once. If no heavy worker is running, heavy commands are held in the outbox (never dropped) until one comes online. Over Azure Service Bus, provision the `heavy-command` topic + subscription up front (as with the default command topic). Topic and subscription names are configurable — the entry named `HeavyCommand` in the `Messaging:Topics` array, defaulting to `heavy-command` / `heavy-command-subscription`.

## The store declares its own schema

The write store brings its tables with it. Derive your write context from `WriteDbContext<TContext>`
and the model already holds the event stream, snapshots, the command log, the outbox and the
integrity anchors — together with the constraints the store's guarantees depend on. Generate a
migration from that context and the database enforces them:

| Table | Constraint | What the database refuses |
|---|---|---|
| `event_stream_entry` | unique over `bucket_id`, `stream_id`, `version` | A second event at the same version of the same stream in the same partition. Two writers that collide lose at the database, not only in the application, and the loser sees a `ConcurrencyException` |
| `snapshot` | unique over `bucket_id`, `stream_id`, `version` | A second snapshot for the same stream version |
| `event_chain_anchor` | unique over `bucket_id`, `sequence_number` | A second integrity anchor at the same sequence number in the same partition |
| `command_log_entry` | index on `bucket_id` | — |
| `outbox_entry` | index on `bucket_id` | — |

The table and column names are the ones the `AddNpgsql*DbContextFactory` registrations produce, which
apply a snake_case naming convention. Keep these constraints in the migration as generated. A schema
you maintain by hand, or with another tool, has to carry them too — without the unique constraint on
the event stream, a version collision is no longer refused by the database.

### A context applies only its own configurations

`WriteDbContext<TContext>`, `ReadDbContext<TContext>` and `IdentityStore<TContext, TUser>` ship in one
assembly, `Stratara.EventSourcing.EntityFrameworkCore`. Each of them applies the entity configurations
of its own store only, filtering `ApplyConfigurationsFromAssembly` by namespace, so a write context
never picks up read-store or identity tables. Call `base.OnModelCreating` when you override it and
you keep that filter.

Your own contexts need the same discipline when they share an assembly. If a write context and a
read context — or any two contexts — live side by side and each picks up configurations with
`ApplyConfigurationsFromAssembly`, give each a namespace predicate:

```csharp
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;

public sealed class LedgerWriteDbContext(DbContextOptions<LedgerWriteDbContext> options)
    : WriteDbContext<LedgerWriteDbContext>(options);

public sealed class LedgerReadDbContext(DbContextOptions<LedgerReadDbContext> options)
    : ReadDbContext<LedgerReadDbContext>(options)
{
    // The namespace that holds this context's IEntityTypeConfiguration<T> classes, and no sibling's.
    private const string ReadModelConfigurations = "Ledger.Persistence.ReadModels";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);   // the framework's read-store configurations, already filtered

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(LedgerReadDbContext).Assembly,
            type => type.Namespace?.StartsWith(ReadModelConfigurations, StringComparison.Ordinal) == true);
    }
}
```

```csharp
builder.Services.AddNpgsqlWriteDbContextFactory<LedgerWriteDbContext>();
builder.Services.AddNpgsqlReadDbContextFactory<LedgerReadDbContext>();
```

Without the predicate, the context takes in its sibling's entity configurations and its model gains
tables that belong to another context. The migrations you generated for it no longer match that
model, and nothing in-process says so: the mismatch shows up only when the context meets a real
database, as pending model changes. An in-memory test database does not catch it.

## Example: a worker that runs everything

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddCommandWorkerServices();
builder.Services.AddHeavyCommandWorker(degreeOfParallelism: 2);   // dedicated lane for IHeavyCommand
builder.AddEventProjectionWorkerServices();
builder.AddSagaWorkerServices();
builder.AddOutboxWorkerServices();
builder.AddEventStreamHashWorkerServices();

builder.Services
    .AddCommandHandlersFromAssemblyContaining<MyAggregateMarker>()
    .AddProjectionsFromAssemblyContaining<MyAggregateMarker>()
    .AddSagasFromAssemblyContaining<MyAggregateMarker>()
    .AddAggregatesFromAssemblyContaining<MyAggregateMarker>();

await builder.Build().RunAsync();
```

## Example: an HTTP host

<!-- stratara-snippet-ignore: MapAccountEndpoints is the reader's own endpoint group -->
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddBackendServices();
builder.Services.AddCommandHandlersFromAssemblyContaining<Program>();
builder.Services.AddQueryHandlersFromAssemblyContaining<Program>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapAccountEndpoints();
app.Run();
```

For the full per-extension cheatsheet, see **[DI Extensions Cheatsheet](../reference/di-extensions-cheatsheet.md)**.
