# Stratara.Projections

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

> **License:** [MIT](../../LICENSE).

Projection runtime for the Stratara event-sourced stack. Discovers `IProjection` implementations in the consumer's application assemblies, dispatches event bundles to them, and applies the resulting change sets atomically via the read-model repository layer.

## What's in the box

| Folder | Contents |
|---|---|
| `Services/` | The runtime, all `internal`: `ProjectionManager` (event-bundle → matching projection-handlers fan-out), `ProjectionHandler` (invokes a projection's matching methods), `ProjectionMethodInvoker` (reflection-cached method-pointer dispatch into consumer projections). You implement `IProjection`; these drive it |
| `Multitenancy/` | `TenantProjection` — the framework's own opinionated tenant aggregate projection. Skip the registration if your application has its own tenancy model |
| `Diagnostics/Extensions/` | Source-generated `LoggerProjectionExtensions`, `LoggerChangeSetExtensions`, `LoggerUpdateExtensions` — typed `[LoggerMessage]` surfaces under the `Stratara.Projection.*` / `Stratara.ChangeSet.*` / `Stratara.Update.*` event-ID bands |

## Quick start

```csharp
// In your EventProjection worker:
builder.Services
    .AddProjectionWorker(builder.Configuration)                       // the runtime + hosted service
    .AddProjectionsFromAssemblyContaining<MyAppProjectionMarker>();   // your IProjection implementations
```

Then implement `IProjection` in your application assembly. The projection manager picks them up automatically.

Both calls are needed: `AddProjectionsFromAssemblyContaining<T>()` only registers your projections —
`AddProjectionWorker(IConfiguration)` registers the manager, the invoker and the hosted service that
consumes the event-bundle subscription. Without it nothing drives your projections. Most hosts get
both from the `AddEventProjectionWorkerServices()` composite in `Stratara.EventSourcing.WorkerDefaults`.

> **No checkpoint store.** Projections are driven push-wise off the event bus; the framework keeps no
> per-projection checkpoint, so there is no consumer-lag metric and no built-in resume-from-sequence.
> Replay is coordinated separately (see `Stratara.Outbox.RabbitMQ`'s replay state).

## Dependencies

- `Stratara.Contracts` — for `EventBundle` + `IEvent<T>`.
- `Stratara.Domain` — for the framework's `Tenant` aggregate (only consumed by `TenantProjection`).
- `Stratara.Shared` — for change-tracking primitives, reflection cache, partitioning helpers, diagnostics base.
- `Microsoft.Extensions.Hosting.Abstractions` + `Microsoft.Extensions.Options.ConfigurationExtensions` — for projection-worker checkpointing options.
- `JetBrains.Annotations` — for static-analysis attributes on projection-handler conventions.
