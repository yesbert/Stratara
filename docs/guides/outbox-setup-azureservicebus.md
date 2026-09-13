---
title: "Outbox Setup: Azure Service Bus"
description: "Wiring the Azure Service Bus message bus with managed identity, and how its administratively provisioned subscriptions differ from RabbitMQ's."
---

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

- **Topics**: named from the `Messaging` configuration section — `command`, `heavy-command`, `event-bundle` and `notifications` by default. See the [RabbitMQ guide](outbox-setup-rabbitmq.md#routing-model) for the full table and the override shape; the names are transport-independent.
- **Subscriptions** per worker host, defaulting to `command-subscription`, `heavy-command-subscription`, `event-bundle-subscription` and `event-bundle-saga-subscription`. Service Bus subscriptions are durable — a worker that's down accumulates messages in its subscription until it reconnects.
- **Provision topics and subscriptions up front.** Unlike RabbitMQ, where the bus declares its own exchanges, Service Bus entities must exist before a worker connects.
- **Consumers per worker.** The projection and saga workers open one consumer per processor on their subscription, so bundles are not processed in publication order; bundles about one aggregate are applied one at a time within the process. `Projections:DegreeOfParallelism` and `Sagas:DegreeOfParallelism` set the count — a value that is not a positive number means the processor count, `1` means strict transport order. The [RabbitMQ guide](outbox-setup-rabbitmq.md#how-many-consumers-a-worker-opens) shows the configuration shape; it is transport-independent.

`EnsureSubscriptionAsync` — the call the [RabbitMQ guide](outbox-setup-rabbitmq.md) shows for closing
the cold-start gap — exists here too and deliberately does nothing. It has nothing to do: your
subscriptions were created by a template or a deployment step long before the process that would call
it started, which is the same end state the RabbitMQ call is reaching for. Code written against one
transport therefore runs unchanged against the other, and the property it depends on holds in both.

It is not left unimplemented by oversight, and it should not be "fixed" into a management-client call:
the credentials a running host holds are data-plane credentials, and creating entities from them is a
different permission and a different lifecycle.

## Processor tuning

The subscription processor runs with the Azure SDK's default `ServiceBusProcessorOptions` —
Stratara does not currently surface `MaxConcurrentCalls`, `PrefetchCount`, or the lock-renewal
duration as configuration. If you need to tune those, they live on the `ServiceBusClient` /
processor from `Azure.Messaging.ServiceBus`; treat this as an integration point you own rather than
a knob Stratara exposes.

## DLQ + retries

The framework decides when a message is dead-lettered, not the broker. A message whose handler throws
is abandoned for redelivery until the bound for its failure kind is reached, then dead-lettered with a
reason you can filter on — `failure` for a handler exception, `conflict` for a concurrency conflict
that never resolved — and the exception in the description. The bounds are the same on RabbitMQ and
come from one section:

```jsonc
{
  "MessageRetry": {
    "MaxDeliveryAttempts": 3,    // handler failures: delivered 3 times, then dead-lettered
    "MaxConflictRequeues": 100   // concurrency conflicts: abandoned 100 times, then dead-lettered
  }
}
```

The framework reads the message's `DeliveryCount`. The subscription's own `MaxDeliveryCount` is a
backstop and must be **at least one above the larger bound** (101 with the defaults), or the broker
dead-letters first with its own reason and the framework's log and counter never fire. Where the
host's credentials can read the subscription's definition, the bus logs a warning (`108_111`) at
subscribe time when the limit is too low; a host with data-plane-only rights skips the check.

Every dead-lettering is logged (`108_110`) and counted on `messaging.dead_lettered`, tagged with
`messaging.topic`, `messaging.subscription` and `reason`. Alert on the counter or on the broker's
`Active Messages in DLQ`; return a message with the portal, the CLI or a peek-and-resubmit, and it
arrives as a fresh delivery.

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
