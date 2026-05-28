# Stratara.Sagas

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Saga runtime for the Stratara event-sourced stack. Discovers `ISaga` implementations in the consumer's application assemblies, dispatches event bundles to them, and runs them under the `SagaWorker` hosted service.

## What's in the box

| Folder | Contents |
|---|---|
| `Abstractions/` | `ISaga` (marker for handler discovery), `ISagaManager`, `ISagaHandler`, `ISagaMethodInvoker` |
| `Services/` | `SagaManager` (event-bundle → matching saga fan-out), `SagaHandler` (per-saga scoped execution + retries), `SagaMethodInvoker` (reflection-cached method-pointer dispatch into consumer sagas), `SagaWorker` (hosted service consuming the event-bundle subscription), `SagaOptions` (subscription + concurrency knobs) |
| `DependencyInjection/` | `AddSagaWorker(IConfiguration)` + `AddSagasFromAssemblyContaining<T>()` |

## Quick start

```csharp
// In your Saga worker:
builder.Services.AddSagaWorker(builder.Configuration);
builder.Services.AddSagasFromAssemblyContaining<MyAppSagaMarker>();
```

Then implement `ISaga` in your application assembly. The saga manager picks them up automatically and dispatches matching events to their handler methods.

```csharp
public sealed class OrderShippingSaga(ICommandOutboxDispatcher dispatcher) : ISaga
{
    // Discovered via reflection — handler methods can be public or private.
    [JetBrains.Annotations.UsedImplicitly]
    private async Task HandleAsync(OrderPaidEvent @event, CancellationToken ct) =>
        await dispatcher.EnqueueCommandAsync(new ScheduleShipmentCommand(@event.OrderId), ct);
}
```

## How sagas work

### Lifecycle

- **Scoped per event bundle.** Saga instances are resolved via `IServiceScopeFactory.CreateScope()` for every event bundle the `SagaWorker` consumes from the message bus. A fresh DI scope means transient dependencies (`DbContext`, repositories, the unit of work) are isolated per dispatch.
- **No durable instance state.** Sagas are *not* persisted between bundles — Stratara does not keep a per-saga state row. Anything you need to remember across bundles must be persisted externally (event store, read store, the aggregate the saga decides about, or a dedicated saga-state aggregate).
- **At-least-once dispatch.** The underlying event bundle subscription is at-least-once (see the `Outbox` package). Handler methods MUST be idempotent — typically by checkpointing the latest processed event version per stream, or by deduplicating against the event id.

### Correlation

- **Routing key = event type.** The `SagaManager` filters incoming bundles by the relevant event types each saga declares (via its `HandleAsync` method signatures). A saga only ever sees events it asked for.
- **Correlation across events** is *your* responsibility: typically you use the aggregate id carried on the event (or a domain key like `OrderId`) to look up state, decide what to do, and emit a follow-up command via `ICommandOutboxDispatcher`.
- The session context (correlation id, causation id, actor, subject) is restored from the wire envelope *before* handlers run, so source-generated `LoggerSagaExtensions` calls inside a handler are automatically scoped to the originating session.

### State management

Sagas should drive state changes by **emitting commands**, not by mutating shared state directly. The recommended pattern:

```csharp
[UsedImplicitly]
private async Task HandleAsync(ShipmentScheduledEvent @event, CancellationToken ct)
{
    var view = await readStore.GetOrderShippingViewAsync(@event.OrderId, ct);
    if (view is { State: ShippingState.AwaitingDispatch })
    {
        await dispatcher.EnqueueCommandAsync(new MarkOrderInTransitCommand(@event.OrderId), ct);
    }
}
```

The read view holds the current state, the command (via the outbox) advances it, and the next event triggers the next saga step. This keeps sagas stateless, testable, and replay-safe.

### Annotations on consumer handlers

Handler methods are discovered via reflection, so static analyzers (R#, IDE, Roslyn) flag them as unused. Mark every handler with `[JetBrains.Annotations.UsedImplicitly]`:

```csharp
[UsedImplicitly]
private async Task HandleAsync(InvoiceIssuedEvent @event, CancellationToken ct) { … }
```

The class itself (`ISaga` implementation) is registered through `AddSagasFromAssemblyContaining<T>()` and is also "used implicitly" — typically mark it `[UsedImplicitly]` as well, especially if you do not reference it directly elsewhere.

## Dependencies

- `Stratara.Contracts` — for `EventBundle` + `IEvent<T>`.
- `Stratara.Domain` — for the framework's aggregate interfaces (sagas typically dispatch commands referencing tenant-scoped aggregates).
- `Stratara.Shared` — for messaging primitives, the source-generated `LoggerSagaExtensions` diagnostics surface, and DI conventions.
- `Microsoft.Extensions.Hosting.Abstractions` + `Microsoft.Extensions.Options.ConfigurationExtensions` — for `SagaWorker` hosting + `SagaOptions` binding.
- `JetBrains.Annotations` — for static-analysis attributes on saga-handler conventions.
