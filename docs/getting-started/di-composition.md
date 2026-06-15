# DI Composition

Stratara composes via à-la-carte `Add*Services()` extension methods on `IServiceCollection` and `IHostApplicationBuilder`. A typical host picks two or three, never all of them. Pick by **what shape of work** the host does, not by what packages it references.

## The decision tree

```
┌─ Are you a worker host or an HTTP host?
│
├─ HTTP host (ASP.NET Core minimal-API / MVC)
│  └─→ builder.AddBackendServices()                 (Mediator, Identity, Session, Security, Resilience)
│      builder.Services.MapStrataraDefaults()        (health endpoints + OpenAPI)
│
└─ Worker host (background service)
   ├─ Need to handle commands?         → builder.AddCommandWorkerServices()
   ├─ Need a dedicated lane for slow,  → builder.AddHeavyCommandWorkerServices(dop?)
   │  long-running commands?              (drains IHeavyCommand — see "Opt-in: heavy-command lane")
   ├─ Need to run projections?         → builder.AddEventProjectionWorkerServices()
   ├─ Need to run sagas?               → builder.AddSagaWorkerServices()
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

`degreeOfParallelism` bounds how many heavy commands run at once. If no heavy worker is running, heavy commands are held in the outbox (never dropped) until one comes online. Over Azure Service Bus, provision the `heavy-command` topic + subscription up front (as with the default command topic). Topic/subscription names are configurable under `Messaging:HeavyCommand`.

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

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddBackendServices();
builder.Services.AddCommandHandlersFromAssemblyContaining<Program>();
builder.Services.AddQueryHandlersFromAssemblyContaining<Program>();

var app = builder.Build();
app.MapStrataraDefaults();
app.MapAccountEndpoints();
app.Run();
```

For the full per-extension cheatsheet, see **[DI Extensions Cheatsheet](../reference/di-extensions-cheatsheet.md)**.
