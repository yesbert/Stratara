# Stratara.EventSourcing.WorkerDefaults

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

One-stop `IHostApplicationBuilder` composites that wire together the Stratara event-sourced stack for each host shape (API, command worker, event-projection worker, saga worker, event-stream-hash worker, outbox worker). Reference this package from each host instead of repeating the per-concern DI call chain.

## What's in the box

| Composite | Wires |
|---|---|
| `AddBackendServices` | Common framework services (messaging, identity, session, security, mapping, resilience) + mediator + write store + outbox dispatcher. For API hosts that dispatch commands but don't process them. |
| `AddCommandWorkerServices` | Common + mediator + mediator-worker (hosted service that consumes the command topic into the in-process mediator) + write store + event sourcing + outbox dispatcher. |
| `AddEventProjectionWorkerServices` | Common + write store + projection-replay state + projection worker. |
| `AddSagaWorkerServices` | Common + write store + event sourcing + outbox dispatcher + saga worker. |
| `AddEventStreamHashWorkerServices` | Common + write store + event-stream-hashing worker. |
| `AddOutboxWorkerServices` | Common + write store + outbox dispatcher + outbox-retry worker. |

The composites live in the `Microsoft.Extensions.Hosting` namespace so call sites read naturally:

```csharp
// Command-handling worker host:
builder.AddCommandWorkerServices();

// API host that only dispatches commands:
builder.AddBackendServices();
```

## Dependencies

This package pulls the framework's Tier-C pieces (`Stratara.Infrastructure`, `Stratara.Outbox.RabbitMQ`, `Stratara.Projections`, `Stratara.Sagas`, `Stratara.EventSourcing.EntityFrameworkCore`) so a single PackageReference is enough for a worker host. The lean `Stratara.ServiceDefaults` (OTel + Serilog) and `Stratara.ServiceDefaults.AspNetCore` (health-check endpoints + ASP.NET OTel) stay separate so non-worker hosts don't drag the Tier-C runtime in.
