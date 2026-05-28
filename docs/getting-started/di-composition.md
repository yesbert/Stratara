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
   ├─ Need to handle commands?    → builder.AddCommandWorkerServices()
   ├─ Need to run projections?    → builder.AddEventProjectionWorkerServices()
   ├─ Need to run sagas?          → builder.AddSagaWorkerServices()
   ├─ Need to hash event streams? → builder.AddEventStreamHashWorkerServices()
   └─ Need to drain the outbox?   → builder.AddOutboxWorkerServices()
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

## Example: a worker that runs everything

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddCommandWorkerServices();
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
