---
title: "Outbox Setup: RabbitMQ"
description: "Wiring the RabbitMQ message bus with publisher confirms, automatic reconnect and mandatory routing, and what happens to a message that cannot be delivered."
---

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

The bus reads its connection from one of two places, and there is no `RabbitMq` section — a
section by that name is silently ignored. Either a connection string named `rabbitmq`:

```jsonc
// appsettings.json
{
  "ConnectionStrings": {
    "rabbitmq": "amqp://guest:guest@localhost:5672/"
  }
}
```

or four flat configuration keys, Kubernetes-style, which take precedence when `RABBITMQ_HOST` is set:

```bash
RABBITMQ_HOST=rabbitmq
RABBITMQ_PORT=5672            # default 5672
RABBITMQ_USERNAME=app         # outside Development both credentials are mandatory
RABBITMQ_PASSWORD=…           # in Development only, guest/guest is the fallback
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
  stage, a rebuilt host, a CI run against a fresh broker. (The queue behind a worker subscription is
  named `<subscription>.v2` — see [When a handler cannot take a message](#when-a-handler-cannot-take-a-message).)
- **Take the names from `IMessagingIdentifier`, not from strings.** A subscription you forget is a
  subscription that keeps losing messages, silently, and a typo produces a second queue nobody reads.
- **Nothing calls this for you.** The framework cannot know your start-up order or which processes
  publish, so it makes no attempt to guess. If your host runs every worker in one process and nothing
  publishes before the host is serving, you may not need it at all.

### How many consumers a worker opens

The projection worker and the saga worker each open several consumers on their subscription — one
per processor by default — and the broker deals consecutive bundles to different ones, so bundles are
**not** processed in publication order. What the workers guarantee is that bundles about one
aggregate are applied one at a time within the process; see [Write a projection](write-a-projection.md)
for what a handler may rely on and how it says "not yet".

Both counts are configuration:

```json
{
  "Projections": { "DegreeOfParallelism": 1 },
  "Sagas": { "DegreeOfParallelism": 1 }
}
```

A value that is not a positive number — including leaving the key out — means one consumer per
processor. `1` gives a worker that applies every bundle in the order the transport delivers it, at
the cost of the parallelism; it is the right setting for a host that needs strict order, and the
wrong one for a host that only needs the per-aggregate guarantee, which it already has.

**What it costs.** Once a subscription is established, messages published to it are kept until
something consumes them — where before they were dropped. If you establish a subscription for a
worker you then never deploy, its queue grows. That is the trade this makes deliberately: a fact kept
somewhere you must clear is better than a fact silently gone.

Client subscriptions (`default-*`) are refused: they are declared exclusive and auto-deleting, so a
queue established ahead of its consumer would be removed the moment the declaring channel closed.
Establishing one would look like it worked and retain nothing.

## Durable bundles: closing the commit-to-publish window

By default a save commits its events and then tries the bus; the bundle reaches the outbox table only
if the bus refuses it. Between the commit and that publish there is a window in which the process
may end — and a bundle lost there is lost for every subscription: the events are in the store, and no
projection and no saga is told. Projections recover by a replay; a saga has no such repair.

`Outbox:DurableBundles` closes the window. The bundle is written to the outbox table **in the same
transaction that commits its events**, published after the commit, and removed once the bus has
accepted it. A process that ends anywhere after the commit leaves a row the outbox worker delivers.

```jsonc
{
  "Outbox": {
    "DurableBundles": true,       // default false: bus-first, window open
    "PollingIntervalSeconds": 5   // how soon the worker picks up what a crash left behind
  }
}
```

What it costs: one insert per save inside a transaction that already exists, and one delete per
accepted bundle afterwards. Measured on PostgreSQL against a bus that accepts instantly — an upper
bound on the relative cost, since a real broker slows both paths alike — the option takes
**28–30 % off the appends per second** at 1, 8 and 32 writers alike (2 650 → 1 860 at 32 writers on
the reference machine). On a healthy bus the table stays near empty; a spike in it now means the
bus is down, which it meant before too. Because the worker may drain a stored row before the save's
own removal reaches it, a bundle can occasionally be delivered twice — which at-least-once already
requires every handler to tolerate. Commands dispatched
through `ICommandOutboxDispatcher` are unaffected — a command has no commit to be atomic with, and
bus-first stays right for it. A host running sagas on the bus path, or one that cannot afford a
replay, should switch it on; the outbox worker must run somewhere in the deployment for the rows
a crash leaves to be delivered.

## When a handler cannot take a message

A worker subscription is a **quorum queue** with a dead-letter queue beside it. A message whose
handler throws is redelivered a bounded number of times and then moved to
`<subscription>.dead-letter`, where you can inspect it and return it once the cause is fixed. Nothing
is dropped by the framework. The bounds are the same on Azure Service Bus and come from one
section:

```jsonc
{
  "MessageRetry": {
    "MaxDeliveryAttempts": 3,    // handler failures: delivered 3 times, then dead-lettered
    "MaxConflictRequeues": 100   // concurrency conflicts: requeued 100 times, then dead-lettered
  }
}
```

A concurrency conflict is a retry, not a failure — the next delivery usually sees the new aggregate
version and passes — so it has the larger bound. Redelivery is immediate on both transports; there
is no back-off. The framework reads the delivery count the broker stamps on every redelivery
(`x-acquired-count` from RabbitMQ 4.3, `x-delivery-count` before) and decides itself; the queue's
`x-delivery-limit` sits one above the larger bound as a backstop for redeliveries nobody asked
for — a consumer that died mid-message — which is the only kind RabbitMQ 4.3+ counts against it.
A message the broker dead-letters by that backstop is not in the framework's log or counter.

**Changing the bounds on a running deployment.** RabbitMQ compares `x-delivery-limit` whenever a
queue is declared again and cannot change it on an existing quorum queue. A worker queue that
already exists therefore keeps the limit it was first declared with: the subscription opens and uses
it, and the bus logs a warning (`108_112`) naming the queue and the broker's reply. The new bounds
still decide every redelivery the consumer asks for. From RabbitMQ 4.3 the old limit counts no other
kind, so nothing more is needed. Before 4.3 it counts every redelivery, so a bound raised above the
old limit is cut short by the broker; delete the drained queue and let the next start declare it
again.
The worker queue dead-letters through the default exchange straight to `<subscription>.dead-letter`
with the at-least-once strategy, so the move itself cannot lose the message.

Every dead-lettering is logged (`108_110`) and counted on `messaging.dead_lettered`, tagged with
`messaging.topic`, `messaging.subscription` and `reason` (`conflict` or `failure`). A rising
`conflict` count is contention to look at or a bound to raise; a rising `failure` count is a handler
to fix and a queue to replay.

**Returning a message.** Move it from `<subscription>.dead-letter` back to the worker queue with the
shovel plugin or the management UI. It arrives as a fresh delivery, with its count starting over.

**The queue names changed with this feature.** A worker subscription named `event-bundle-subscription`
now consumes `event-bundle-subscription.v2`; the old classic queue cannot be redeclared as a quorum
queue, and the broker refuses the attempt. The rollout is two steps:

1. Deploy. New consumers bind `<subscription>.v2` to the same exchange; old consumers keep draining
   the old queue, and every bundle reaches both — which at-least-once already requires handlers to
   tolerate.
2. When the old queue is empty and no old consumer is left, delete it. It is still bound to the
   exchange and would otherwise fill with every message forever.

Quorum queues need RabbitMQ 3.8 or later; the framework's integration tests run against 4.x.

**Client subscriptions** (`default-` prefix) are exclusive, auto-deleting queues for a process that is
listening right now; they have no dead-letter queue. A conflict is requeued, any other failure is
dropped — there is nobody to return a message to.

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
