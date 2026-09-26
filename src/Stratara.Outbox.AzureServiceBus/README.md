# Stratara.Outbox.AzureServiceBus

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

> **License:** [MIT](../../LICENSE).

Azure Service Bus implementation of `Stratara.Abstractions.Messaging.IMessageBus`. Publishes JSON-serialized messages to topics and exposes a subscription helper that wires up a Service Bus processor with per-message exception classification:

- success → `CompleteMessageAsync`
- `ConcurrencyException` → `AbandonMessageAsync` until `MessageRetry:MaxConflictRequeues` redeliveries (default 100) have not resolved it, then `DeadLetterMessageAsync` with reason `conflict`
- any other exception → `AbandonMessageAsync` until `MessageRetry:MaxDeliveryAttempts` deliveries (default 3) have failed, then `DeadLetterMessageAsync` with reason `failure` and the exception in the description

The subscription's own `MaxDeliveryCount` must leave room for the bounds; a subscription created with Service Bus defaults allows 10 deliveries. Where the host can read the subscription, the bus lowers the bounds for it to fit and logs `108_111`.

System-level errors (connection drops, auth failures) arrive via `ProcessErrorAsync` and are logged; the Service Bus client owns the reconnect / retry policy for those.

A subscription that stops closes its processor, which takes no further message and waits up to twenty seconds for its running handler; the host waits for that before it counts as stopped, within its shutdown timeout. A handler's outcome is settled whatever the subscription's own cancellation says, and a handler that failed because its save committed but could not publish has its message completed, not delivered again.

## Install

```bash
dotnet add package Stratara.Outbox.AzureServiceBus
```

Register the bus in your DI composition — one call wires the `ServiceBusClient`, the envelope
options and the `IMessageBus` implementation:

```csharp
// Connection string:
builder.Services.AddAzureServiceBus(builder.Configuration.GetConnectionString("ServiceBus")!);

// Or, preferred in Azure — managed identity, no secret in configuration:
builder.Services.AddAzureServiceBusWithManagedIdentity("my-namespace.servicebus.windows.net");
```

The `AzureServiceBusBus` implementation is `internal`; register it through these extensions rather
than by naming the type.

> **The explicitly-chosen transport wins.** `AddAzureServiceBus` *replaces* the `IMessageBus`
> registration, so it takes effect even when the RabbitMQ umbrella (`builder.AddMessaging()`, which
> the worker composites call) already claimed the slot. Still use one transport per host — calling
> both is a configuration smell — but an explicit `AddAzureServiceBus` will no longer be a silent
> no-op behind a composite.

## Notes

Pre-3.0 this implementation lived inside `Stratara.Outbox.RabbitMQ`. As of 3.0 the two transports are separate packages so a consumer who only wants RabbitMQ does not drag the Azure Service Bus SDK into the dependency tree (and vice versa).
