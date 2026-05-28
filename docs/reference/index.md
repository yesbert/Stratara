# Reference

Hand-written reference material (cheatsheets, conventions, schemas) plus the auto-generated API reference.

## Cheatsheets

- **[DI Extensions Cheatsheet](di-extensions-cheatsheet.md)** — full menu of `Add*Services()` extensions per package.
- **[Routing Conventions](routing-conventions.md)** — when to use `ICommand` vs `ICommand<T>` vs `IQuery<T>`, and via `IMediator` vs `ICommandOutboxDispatcher`.
- **[LogEvents Schema](log-events-schema.md)** — event-ID ranges (100_000 framework, 200_000+ consumer apps).

## API Reference

The full surface of every public type, generated from XML doc comments by DocFX `mref`.

Browse via the **API Reference** entry in the left navigation, or jump straight to a top-level namespace:

- `Stratara.Abstractions.Mediator` — core IMediator/ICommand/IQuery contracts.
- `Stratara.Abstractions.EventSourcing` — IEvent, IEventSource, IAggregationService.
- `Stratara.Abstractions.Outbox` — outbox dispatchers + repository contracts.
- `Stratara.Abstractions.Messaging` — bus envelopes + integrity contracts.
- `Stratara.Abstractions.Security` — encryption, key-store, AAD contracts.
