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
    // In Development only, the broker's default `guest/guest` is used.
  }
}
```

**Fail-fast outside Development** (v3.4.0; v3.0.14+ for Production only): if `RABBITMQ_USERNAME` / `RABBITMQ_PASSWORD` are missing on any host that is not in Development, publishing throws `InvalidOperationException` naming the environment. The `guest/guest` fallback is Development-only — same pattern as the key-store guard.

> **Changed in 3.4.0.** The check used to be `IsProduction()`, which recognises exactly one name: Staging, QA, UAT, Preview, anything self-named, and even `Production-EU` and `prod` all fell through to `guest`. RabbitMQ restricts `guest` to localhost by default, so a remote broker refused the connection anyway — but a broker in the same container or network running a default configuration accepted it. If you deliberately want the default account outside Development, set `RABBITMQ_USERNAME=guest` and `RABBITMQ_PASSWORD=guest` explicitly; the configuration is the opt-in.

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

Topic and subscription names come from the `Messaging` configuration section. Every one has a
default, so a host that configures nothing still works:

| Topic | Default name | Default subscription(s) | Who publishes / consumes |
|---|---|---|---|
| `Command` | `command` | `command-subscription` | Your outbox (and other apps) publish; the command worker consumes |
| `HeavyCommand` | `heavy-command` | `heavy-command-subscription` | `IHeavyCommand` commands; the heavy-command worker consumes |
| `EventBundle` | `event-bundle` | `event-bundle-subscription`, `event-bundle-saga-subscription` | The write-store publishes; projection and saga workers consume |
| `Notification` | `notifications` | — | Consumer-defined notification fan-out |

Override any of them by name:

```jsonc
{
  "Messaging": {
    "Topics": [
      {
        "Name": "Command",
        "Value": "myapp.command",
        "Subscriptions": [ { "Name": "CommandSubscription", "Value": "myapp.command.worker" } ]
      }
    ]
  }
}
```

Topics are **fanout exchanges** + per-subscription queues. Multiple worker hosts can scale out by sharing a queue — RabbitMQ does the work-stealing.

## Establish subscriptions before the first publish

A queue exists from the moment something binds it, and RabbitMQ delivers only to queues that already
exist. It does not hold a message for a queue that shows up later.

That is fine while one subscription binds late — the publish reaches nobody, the broker returns it,
and the outbox retries. It is **not** fine when a topic carries more than one subscription, which
`event-bundle` does: projections and sagas share it. If the saga queue is bound and the projection
queue is not, the publish is confirmed, no outbox row is written, nothing retries, and nothing logs.
The projection simply never sees that event.

The window is real. Two workers that start twenty seconds apart — separate deployments, a shared
migration lock, a cold environment — are enough.

Close it from whichever process publishes first, before it publishes anything:

```csharp
var ids = app.Services.GetRequiredService<IMessagingIdentifier>();
var bus = app.Services.GetRequiredService<IMessageBus>();

await bus.EnsureSubscriptionAsync(ids.EventBundleTopic, ids.EventBundleSubscription);
await bus.EnsureSubscriptionAsync(ids.EventBundleTopic, ids.EventBundleSagaSubscription);
```

`EnsureSubscriptionAsync` creates the queue and its binding **without consuming from it**. That is
the whole difference from `SubscribeAsync`, which can only create a queue by also starting to read it
— and therefore only when the worker that reads is ready. Anyone can establish anyone's subscription,
so the publishing host can create a queue for a worker that has not started, or will not start for
another hour.

Three things worth knowing:

- **Only the first time matters.** Worker queues are durable and are not auto-deleted, so once they
  exist they survive restarts of the app and of the broker. This is a cold-environment problem: a new
  stage, a rebuilt host, a CI run against a fresh broker.
- **Take the names from `IMessagingIdentifier`, not from strings.** A subscription you forget is a
  subscription that keeps losing messages, silently, and a typo produces a second queue nobody reads.
- **Nothing calls this for you.** The framework cannot know your start-up order or which processes
  publish, so it makes no attempt to guess. If your host runs every worker in one process and nothing
  publishes before the host is serving, you may not need it at all.

**What it costs.** Once a subscription is established, messages published to it are kept until
something consumes them — where before they were dropped. If you establish a subscription for a
worker you then never deploy, its queue grows. That is the trade this makes deliberately: a fact kept
somewhere you must clear is better than a fact silently gone.

Client subscriptions (`default-*`) are refused: they are declared exclusive and auto-deleting, so a
queue established ahead of its consumer would be removed the moment the declaring channel closed.
Establishing one would look like it worked and retain nothing.

## Backpressure

The `OutboxWorker` polls the outbox table every `OutboxOptions.PollingIntervalSeconds` (default 30) and publishes pending rows. If the broker is unreachable, rows sit in the table — at-least-once delivery preserved. The next poll-cycle retries.

**A cycle takes one batch of each kind and ends.** Rows the broker would not accept stay in the table and are retried on the next interval; a cycle never re-reads what it has just failed to publish. That bounds the work a cycle can do, and it is what stops an unreachable broker — or a suppressed drain during a projection replay — from turning a cycle into a loop over the same rows. The practical consequence: a large accumulated backlog drains at one batch per interval rather than in a single pass. With the defaults that is 20 000 rows a minute, and both knobs below are yours.

`OutboxOptions.BatchSize` (default 10_000) caps how many rows the worker claims per cycle, and `LockLeaseSeconds` (default 60) is how long a claimed batch stays leased to one worker. Bind them under the `Outbox` configuration section.

## Connection health

`Stratara.Outbox.RabbitMQ` uses `RabbitMQ.Client`'s automatic recovery + topology recovery. `NetworkRecoveryInterval` is set to a small default; consumers re-subscribe automatically after a reconnect.

On startup the bus fails fast in Production if the broker connection can't be established, rather than starting a worker that silently publishes nowhere.

## Observability

The outbox plane records the `outbox.published` counter (on the `Stratara.Service` meter), tagged
by entry kind — `command` or `event` — so you can watch command-dispatch and event-bundle throughput
separately. It counts what the broker **accepted**, not what was read from the table: a row that
could not be published is not counted, so the counter going flat while the table stays full is the
signal that dispatch is stuck rather than busy. The `Stratara.ServiceDefaults` OpenTelemetry config wires the `Stratara.Service` meter
and the `Stratara.Application` activity source automatically.

Failure paths (`PublishReturnException` on no-binding, broker-disconnect, …) emit warning-level log events from `Stratara.Shared.Diagnostics.Extensions.LoggerOutboxExtensions` — see the [LogEvents Schema](../reference/log-events-schema.md).
