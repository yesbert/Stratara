# Stratara.Outbox.RabbitMQ

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Outbox-pattern command + event dispatch for the Stratara event-sourced stack with a RabbitMQ / Azure Service Bus message-bus implementation. Contains the write-side dispatchers, the outbox-retry worker, the read-side mediator command worker, the message-bus implementations, and the Redis-backed `ProjectionReplayState` that coordinates dispatch skip during projection replay.

## What's in the box

| Folder | Contents |
|---|---|
| `Outbox/` | `OutboxOptions`, `CommandOutboxDispatcher` (write-side `ICommand` fan-out via `IMessageBus`, falls back to outbox table on bus failure), `EventBundleOutboxDispatcher` (same for `EventBundle`), `OutboxWorker` (hosted service that retries unpublished outbox rows on a polling interval), `NullOutboxLock` + `RedisOutboxLock` (`IOutboxLock` implementations — default no-op for single-instance deployments, Redis-leased distributed lock for multi-replica setups) |
| `Messaging/` | `RabbitMqBus` — `IMessageBus` over RabbitMQ. Azure Service Bus ships as the sibling `Stratara.Outbox.AzureServiceBus` package. |
| `Mediator/` | `MediatorCommandWorker` (hosted service that subscribes to the command topic and dispatches into the in-process `IMediator`) |
| `Projections/` | `ProjectionReplayState` (Redis-backed concrete `IProjectionReplayState`; dispatchers skip publishing while replay is active) |
| `DependencyInjection/` | `AddOutboxDispatcher()`, `AddOutboxWorker(IConfiguration)`, `AddRedisOutboxLock()` (opt-in distributed lock), `AddProjectionReplayState()`, `AddMediatorWorker()`, `AddMessaging()` |
| `Diagnostics/Extensions/` | `LoggerOutboxExtensions`, `LoggerMessagingExtensions` (source-generated logger surfaces) |

## Quick start

```csharp
// In your API host:
builder.AddMessaging();                          // IMessageBus + MessagingOptions binding
builder.Services
    .AddOutboxDispatcher()                       // CommandOutboxDispatcher + EventBundleOutboxDispatcher + ProjectionReplayState
    .AddOutboxWorker(builder.Configuration);     // OutboxWorker hosted service (only if this host owns retries)

// In your command worker:
builder.Services
    .AddMediatorWorker();                        // MediatorCommandWorker hosted service
```

The dispatchers consult `IProjectionReplayState.IsReplayActive` before each publish and skip dispatch (writing to the outbox table only) while a replay is in progress.

## Multi-instance outbox workers

`AddOutboxWorker` registers `NullOutboxLock` as the default `IOutboxLock` — a no-op that preserves the single-instance assumption. For multi-replica deployments call `AddRedisOutboxLock()` afterwards; it replaces the no-op with a Redis-leased lock (`SET stratara:outbox:lock NX EX`) so only one replica drains at a time:

```csharp
builder.AddCaching();                              // registers IConnectionMultiplexer
builder.Services
    .AddOutboxDispatcher()
    .AddOutboxWorker(builder.Configuration)
    .AddRedisOutboxLock();                         // multi-replica safe
```

The lease defaults to 60 s (`OutboxOptions.LockLeaseSeconds`). Tune it so it exceeds the worst-case drain duration; otherwise the lock can expire mid-cycle and a peer may start a concurrent drain. Outbox semantics are still at-least-once, so a duplicate publish is recoverable provided handlers stay idempotent.

## Dependencies

- `Stratara.Abstractions` — for `ICommand`, `IEvent`, `IMessageBus`, `ICommandOutboxDispatcher`, `IEventBundleOutboxDispatcher`, `IProjectionReplayState`, `IMessagingIdentifier`, `IWriteUnitOfWork` (used at runtime via the outbox repository).
- `Stratara.Contracts` — for `EventBundle` + `CommandEnvelope` messages.
- `Stratara.Mediator` — `MediatorCommandWorker` dispatches into the in-process `IMediator`.
- `Stratara.Sessions` — dispatcher hydrates `CommandEnvelope` from the current session context.
- `Stratara.Shared` — for messaging primitives, resilience pipeline names, mapping helpers, and the diagnostics base.
- `RabbitMQ.Client`, `StackExchange.Redis` (replay-state + optional outbox-lock).
- `Microsoft.Extensions.Hosting.Abstractions` + `Microsoft.Extensions.Options.ConfigurationExtensions` — for hosted services + options binding.

> The outbox dispatcher persists rows through `IWriteUnitOfWork.CreateOutboxRepository` — that interface lives in `Stratara.Abstractions`, but the concrete implementation comes from `Stratara.EventSourcing.EntityFrameworkCore`. Reference that package alongside this one to get a working stack.
