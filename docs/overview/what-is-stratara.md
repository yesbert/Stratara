# What is Stratara

**Stratara takes the boring decisions for you so you can spend your time on aggregates and use-cases — not on wiring an outbox to a mediator to an event store.**

It's a family of 22 NuGet packages for .NET 10 — application-agnostic, lockstep-versioned, opt-in à la carte. Use as little or as much as you need.

What sets it apart from "compose Marten + Wolverine + MassTransit yourself" is the **integration** plus two properties that none of the standalone libraries ship today:

- **Tamper-evident event streams** — see [Tamper-Evident Streams](../concepts/tamper-evident-streams.md).
- **Tenant-aware field encryption** — see [Tenant-Aware Encryption](../concepts/tenant-aware-encryption.md).

## What you get

- **In-process mediator** with pipeline behaviors (authorization, validation, command-audit, retry).
- **Outbox-pattern dispatcher** for async messaging via RabbitMQ or Azure Service Bus, with publisher-confirms and broker-reconnect.
- **Event store** on PostgreSQL via EF Core — write store, read store, identity store; snapshot tables, command-log, outbox, event-stream entries.
- **Hash-chained integrity worker** that verifies the event stream wasn't mutated post-commit.
- **Field-level encryption** with `[EncryptData]` — AES-GCM with tenant-bound AAD, transparent serialization-boundary seal.
- **Projection runtime** + **saga runtime** that consume event bundles from the bus.
- **Channel-agnostic identity** (sign-in manager + auth-state provider abstractions usable from ASP.NET, MAUI, console).
- **Observability defaults** — OpenTelemetry traces + metrics, Serilog log enrichment, source-generated `[LoggerMessage]` extensions.
- **Polly-backed resilience** via named pipelines.

## What you don't get

Stratara is **application-agnostic**. It does not:

- Define your aggregates (you write your own `Customer`, `Order`, `Tenant`).
- Make decisions about your domain events (you choose the event shape; Stratara persists them).
- Lock you into a specific HTTP layer (the `Mediator` doesn't care if you're behind ASP.NET, gRPC, or a CLI).
- Provide UI primitives — channel-agnostic identity is one example; Blazor/MAUI-specific glue stays in your app.

The architecture is strict: **no consumer-specific code** lives in the framework. If a feature that's specific to one application would simplify Stratara, it doesn't get added — it stays in the consumer.

## Who it's for

- Teams building **multi-tenant event-sourced .NET services** who want the boilerplate decided.
- Apps with a clear **write/read separation** that benefit from CQRS routing.
- Hosts that need **async command dispatch with at-least-once delivery** via outbox + broker.
- Teams that already use **OpenTelemetry + Serilog** and want the schemas pre-registered.

## Who it's not for

- Pure-CRUD apps without an event-store benefit — the framework's weight isn't justified.
- Teams that want a complete domain framework (Stratara intentionally stays beneath the domain layer).
- Hosts that need synchronous fan-out with strict ordering guarantees — outbox + async dispatch is the default Stratara routing.

## How it's structured

Twenty packages organized into three tiers — see **[Architecture at a glance](architecture-at-a-glance.md)** for the diagram + dependency rules.

## License + versioning

- Source-available under [FSL-1.1-MIT](https://fsl.software/) — converts to MIT after 2 years.
- Lockstep versioning — all 22 packages ship at the same `<VersionPrefix>`.
- See `CHANGELOG.md` in the repo root for release notes.
