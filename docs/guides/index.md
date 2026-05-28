# Guides

Task-oriented how-tos for the most common Stratara operations. Each guide assumes you've worked through **[Getting Started](../getting-started/index.md)** and at least the first sample.

## Domain wiring

- **[Write a Command Handler](write-a-command-handler.md)** — `ICommandHandler<T>` + DI registration.
- **[Write a Projection](write-a-projection.md)** — read-side stores driven by event bundles.
- **[Write a Saga](write-a-saga.md)** — process managers that fan one event into many commands.

## Security

- **[Encrypt Sensitive Data](encrypt-data-setup.md)** — `[EncryptData]` + AES-GCM + tenant-aware AAD.
- **[Authorization Decorators](auth-decorators.md)** — `[RequireRole]` + `AuthorizingMediator`.
- **[Bus-Envelope Integrity (HMAC)](hmac-bus-envelope.md)** — opt-in tamper protection on the message bus.

## Infrastructure

- **[Outbox — RabbitMQ](outbox-setup-rabbitmq.md)** — broker setup + worker wiring.
- **[Outbox — Azure Service Bus](outbox-setup-azureservicebus.md)** — managed-identity setup.

## Test discipline

- **[Testing Patterns](testing-patterns.md)** — xUnit v3 MTP idioms, integration-test boundary, test-fakes.
