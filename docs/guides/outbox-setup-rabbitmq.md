# Outbox + RabbitMQ Setup

> **Derived page.** The behaviour described here is specified by the `outbox-and-messaging` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

`Stratara.Outbox.RabbitMQ` provides the `IMessageBus` implementation backed by a RabbitMQ broker. It uses **publisher confirms** + **automatic reconnect** + **mandatory routing** — failed-to-deliver messages are caught + retried from the outbox table.

## Add the package

```bash
dotnet add package Stratara.Outbox.RabbitMQ
```

## Configure

```jsonc
// appsettings.json
{
  "RabbitMq": {
    "HostName": "localhost",
    "Port": 5672,
    "VirtualHost": "/"
    // Username + Password come from env vars in production:
    //   RABBITMQ_USERNAME, RABBITMQ_PASSWORD
    // In Development the broker's default `guest/guest` is used.
  }
}
```

**Production fail-fast** (v3.0.14+): if `RABBITMQ_USERNAME` / `RABBITMQ_PASSWORD` are missing when `IHostEnvironment.IsProduction()`, the host throws `InvalidOperationException` at startup. The `guest/guest` fallback is restricted to Development/Staging — same pattern as the `DummyKeyStore` guard.

## Wire the worker

A typical worker host wires both the outbox-drainer and the command consumer:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddOutboxWorkerServices();   // drains outbox_entry → publishes to bus
builder.AddCommandWorkerServices();  // subscribes to bus → dispatches commands

builder.Services.AddCommandHandlersFromAssemblyContaining<MyCommandMarker>();

await builder.Build().RunAsync();
```

## Routing model

- **Command topic** — `stratara.commands.{appName}`. Producers (other apps + your own outbox) write here. The `CommandWorker` consumes.
- **Event-bundle topic** — `stratara.events.{appName}`. Producers: the write-store. Consumers: projections + sagas (`EventProjectionWorker`, `SagaOrchestrationWorker`).

Both topics are **fanout exchanges** + per-app queues. Multiple worker hosts can scale out by sharing a queue — RabbitMQ does the work-stealing.

## Backpressure

The `OutboxWorker` polls the outbox table every `OutboxOptions.PollingIntervalSeconds` (default 30) and publishes pending rows. If the broker is unreachable, rows sit in the table — at-least-once delivery preserved. The next poll-cycle retries.

`OutboxOptions.BatchSize` (default 10_000) caps how many rows the worker claims per cycle, and `LockLeaseSeconds` (default 60) is how long a claimed batch stays leased to one worker. Bind them under the `Outbox` configuration section.

## Connection health

`Stratara.Outbox.RabbitMQ` uses `RabbitMQ.Client`'s automatic recovery + topology recovery. `NetworkRecoveryInterval` is set to a small default; consumers re-subscribe automatically after a reconnect.

On startup the bus fails fast in Production if the broker connection can't be established, rather than starting a worker that silently publishes nowhere.

## Observability

The outbox worker records the `outbox.published` counter (on the `Stratara.Service` meter), tagged
by entry kind — `command` or `event` — so you can watch command-dispatch and event-bundle throughput
separately. The `Stratara.ServiceDefaults` OpenTelemetry config wires the `Stratara.Service` meter
and the `Stratara.Application` activity source automatically.

Failure paths (`PublishReturnException` on no-binding, broker-disconnect, …) emit warning-level log events from `Stratara.Shared.Diagnostics.Extensions.LoggerOutboxExtensions` — see the [LogEvents Schema](../reference/log-events-schema.md).
