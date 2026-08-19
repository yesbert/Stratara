# Outbox + Azure Service Bus Setup

> **Derived page.** The behaviour described here is specified by the `outbox-and-messaging` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

`Stratara.Outbox.AzureServiceBus` provides the `IMessageBus` implementation backed by Azure Service Bus. Choose this over the RabbitMQ flavour when running on Azure with managed identity — no connection strings on disk.

## Add the package

```bash
dotnet add package Stratara.Outbox.AzureServiceBus
```

## Two registration modes

### 1. Connection string (Development / staging)

```csharp
services.AddAzureServiceBus(builder.Configuration.GetConnectionString("ServiceBus")!);
```

`AddAzureServiceBus` takes the connection string directly and wires the `ServiceBusClient`, the
envelope options, and the `IMessageBus` implementation in one call.

### 2. Managed identity (Production-recommended, v3.0.13+)

```csharp
services.AddAzureServiceBusWithManagedIdentity("myappnamespace.servicebus.windows.net");
```

`AddAzureServiceBusWithManagedIdentity()` resolves via `DefaultAzureCredential` — picks up the host's managed identity, a developer's `az login` session, or a service-principal env var, in that priority. No secrets in your code or appsettings.

## Wire the worker

Identical to the RabbitMQ flavour — pick one outbox provider per host:

```csharp
builder.AddOutboxWorkerServices();
builder.AddCommandWorkerServices();
```

> **One transport per host — the explicit one wins.** `AddAzureServiceBus` *replaces* the
> `IMessageBus` registration, so an explicit call takes effect even when a worker composite already
> wired the RabbitMQ umbrella (`AddMessaging()`). Registration order no longer decides the transport,
> but mixing the two in one host is still a configuration smell — pick one.

## Routing model

- **Topics**: `stratara.commands.{appName}`, `stratara.events.{appName}`.
- **Subscriptions** per worker host. Service Bus subscriptions are durable — a worker that's down accumulates messages in its subscription until it reconnects.

## Processor tuning

The subscription processor runs with the Azure SDK's default `ServiceBusProcessorOptions` —
Stratara does not currently surface `MaxConcurrentCalls`, `PrefetchCount`, or the lock-renewal
duration as configuration. If you need to tune those, they live on the `ServiceBusClient` /
processor from `Azure.Messaging.ServiceBus`; treat this as an integration point you own rather than
a knob Stratara exposes.

## DLQ + retries

Azure Service Bus has built-in dead-lettering. Stratara doesn't override it — when a message exceeds `MaxDeliveryCount` (default 10), it lands in the DLQ. Configure alerts on `Active Messages in DLQ` for your subscriptions.

The Stratara `OutboxWorker` itself only sees the *outbox table*, not the Service Bus delivery counts. A persistent broker failure causes outbox rows to sit unpublished — they don't get dead-lettered, they just wait.

## When to pick Azure Service Bus over RabbitMQ

| | RabbitMQ | Azure Service Bus |
|---|---|---|
| Self-hosted | ✅ | ❌ (managed-only) |
| Per-message ordering | best-effort per queue | strict FIFO via session-ids |
| Free tier | unlimited (self-hosted) | basic tier per-message-billed |
| Managed-identity auth | ❌ (username/password) | ✅ |
| Bus message size | 128 KB (default config) | 256 KB standard / 1 MB premium |

For Azure-native hosts: Service Bus. For self-hosted / on-prem / multi-cloud: RabbitMQ.
