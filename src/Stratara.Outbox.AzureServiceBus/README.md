# Stratara.Outbox.AzureServiceBus

> **License:** [MIT](../../LICENSE).

Azure Service Bus implementation of `Stratara.Abstractions.Messaging.IMessageBus`. Publishes JSON-serialized messages to topics and exposes a subscription helper that wires up a Service Bus processor with per-message exception classification:

- success → `CompleteMessageAsync`
- `ConcurrencyException` → `AbandonMessageAsync` (Service Bus redelivers)
- any other exception → `DeadLetterMessageAsync` (explicit DLQ with the exception type as reason)

System-level errors (connection drops, auth failures) arrive via `ProcessErrorAsync` and are logged; the Service Bus client owns the reconnect / retry policy for those.

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
