# Outbox + Azure Service Bus Setup

`Stratara.Outbox.AzureServiceBus` provides the `IMessageBus` implementation backed by Azure Service Bus. Choose this over the RabbitMQ flavour when running on Azure with managed identity — no connection strings on disk.

## Add the package

```bash
dotnet add package Stratara.Outbox.AzureServiceBus
```

## Two registration modes

### 1. Connection string (Development / staging)

```csharp
services.AddAzureServiceBus(options =>
{
    options.ConnectionString = builder.Configuration["AzureServiceBus:ConnectionString"];
});
```

### 2. Managed identity (Production-recommended, v3.0.13+)

```csharp
services.AddAzureServiceBusWithManagedIdentity(options =>
{
    options.FullyQualifiedNamespace = "myappnamespace.servicebus.windows.net";
});
```

`AddAzureServiceBusWithManagedIdentity()` resolves via `DefaultAzureCredential` — picks up the host's managed identity, a developer's `az login` session, or a service-principal env var, in that priority. No secrets in your code or appsettings.

## Wire the worker

Identical to the RabbitMQ flavour — pick one outbox provider per host:

```csharp
builder.AddOutboxWorkerServices();
builder.AddCommandWorkerServices();
```

## Routing model

- **Topics**: `stratara.commands.{appName}`, `stratara.events.{appName}`.
- **Subscriptions** per worker host. Service Bus subscriptions are durable — a worker that's down accumulates messages in its subscription until it reconnects.

## Configuration knobs

| Key | Default | Effect |
|---|---|---|
| `MaxConcurrentCalls` | 4 | How many messages the worker processes in parallel |
| `MaxAutoLockRenewalDuration` | 5 min | How long the worker holds a peek-lock |
| `PrefetchCount` | 16 | How many messages the SDK pre-fetches |

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
