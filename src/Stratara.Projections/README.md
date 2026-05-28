# Stratara.Projections

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Projection runtime for the Stratara event-sourced stack. Discovers `IProjection` implementations in the consumer's application assemblies, dispatches event bundles to them, and applies the resulting change sets atomically via the read-model repository layer.

## What's in the box

| Folder | Contents |
|---|---|
| `Services/` | `ProjectionManager` (event-bundle → matching projection-handlers fan-out), `ProjectionHandler<TEvent>` base class, `ProjectionMethodInvoker` (reflection-cached method-pointer dispatch into consumer projections), checkpoint plumbing |
| `Multitenancy/` | `TenantProjection` — the framework's own opinionated tenant aggregate projection. Skip the registration if your application has its own tenancy model |
| `Diagnostics/Extensions/` | Source-generated `LoggerProjectionExtensions`, `LoggerChangeSetExtensions`, `LoggerUpdateExtensions` — typed `[LoggerMessage]` surfaces under the `Stratara.Projection.*` / `Stratara.ChangeSet.*` / `Stratara.Update.*` event-ID bands |

## Quick start

```csharp
// In your EventProjection worker:
builder.Services.AddProjectionsFromAssemblyContaining<MyAppProjectionMarker>();
```

Then implement `IProjection` in your application assembly. The projection manager picks them up automatically.

## Dependencies

- `Stratara.Contracts` — for `EventBundle` + `IEvent<T>`.
- `Stratara.Domain` — for the framework's `Tenant` aggregate (only consumed by `TenantProjection`).
- `Stratara.Shared` — for change-tracking primitives, reflection cache, partitioning helpers, diagnostics base.
- `Microsoft.Extensions.Hosting.Abstractions` + `Microsoft.Extensions.Options.ConfigurationExtensions` — for projection-worker checkpointing options.
- `JetBrains.Annotations` — for static-analysis attributes on projection-handler conventions.
