# Stratara.Outbox.AzureServiceBus

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Azure Service Bus implementation of `Stratara.Abstractions.Messaging.IMessageBus`. Publishes JSON-serialized messages to topics and exposes a subscription helper that wires up a Service Bus processor with per-message exception classification:

- success → `CompleteMessageAsync`
- `ConcurrencyException` → `AbandonMessageAsync` (Service Bus redelivers)
- any other exception → `DeadLetterMessageAsync` (explicit DLQ with the exception type as reason)

System-level errors (connection drops, auth failures) arrive via `ProcessErrorAsync` and are logged; the Service Bus client owns the reconnect / retry policy for those.

## Install

```bash
dotnet add package Stratara.Outbox.AzureServiceBus
```

Register the bus in your DI composition:

```csharp
builder.Services.AddSingleton<ServiceBusClient>(_ =>
    new ServiceBusClient(builder.Configuration.GetConnectionString("ServiceBus")!));
builder.Services.AddSingleton<IMessageBus, AzureServiceBusBus>();
```

## Notes

Pre-3.0 this implementation lived inside `Stratara.Outbox.RabbitMQ`. As of 3.0 the two transports are separate packages so a consumer who only wants RabbitMQ does not drag the Azure Service Bus SDK into the dependency tree (and vice versa).
