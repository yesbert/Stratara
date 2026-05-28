# Architecture at a glance

Stratara ships **20 packages** organized into three tiers. Each tier may only depend on tiers **at or below** its own number. The dependency direction is enforced by ProjectReferences in the repo — no cyclic references, no consumer references.

## Tier layout

```
Tier-A  (foundational — no inbound deps from B or C)
├── Stratara.Abstractions     IMediator / ICommand / IQuery / IAggregate / ITenantAggregate
│                              + Persistence/Outbox/Messaging/Session/Security/Auth contracts
├── Stratara.Contracts        Wire-level POCO records (EventMessage, EventBundle, CommandEnvelope, …)
├── Stratara.Diagnostics      ActivitySource + Meter + LogEvent-ID schema (100_000 range)
├── Stratara.Resilience       Polly named pipelines via AddResiliencePipelines()
└── Stratara.Sessions         Actor / Subject SessionContext + ASP.NET middleware

Tier-B  (builds on Tier-A only)
├── Stratara.Mediator         AuthorizingMediator, CommandOutboxDispatcher, pipeline behaviors
├── Stratara.Domain           Tenant aggregate + lifecycle events
├── Stratara.Shared           Umbrella re-export + source-gen Logger* extensions
└── Stratara.ServiceDefaults  OpenTelemetry + Serilog defaults (lean Tier-B)

Tier-C  (builds on Tier-B + Tier-A)
├── Stratara.EventSourcing.EntityFrameworkCore       WriteStore + ReadStore + IdentityStore (folded)
├── Stratara.EventSourcing.Pipeline.CommandAudit     Command-audit pipeline behavior
├── Stratara.EventSourcing.WorkerDefaults            6 worker composites
├── Stratara.Projections                             Projection runtime + ProjectionManager
├── Stratara.Sagas                                   ISaga interface + saga dispatcher
├── Stratara.Outbox.RabbitMQ                         RabbitMQ outbox + worker
├── Stratara.Outbox.AzureServiceBus                  Azure Service Bus IMessageBus impl
├── Stratara.Infrastructure                          Auth decorators + DI composition glue
├── Stratara.Identity.Core                           Channel-agnostic identity primitives
├── Stratara.Identity.AspNetCore                     ASP.NET Core identity wiring
└── Stratara.ServiceDefaults.AspNetCore              ASP.NET OTel + health + endpoints
```

## Read the tiers as a dependency-direction promise

- **Tier-A is pure contracts.** Adding a class here means committing to an API surface for the lifetime of the major version.
- **Tier-B implements Tier-A interfaces** that are general enough to not need infrastructure (mediator, domain primitives).
- **Tier-C does the rest** — anything touching EF Core, a broker, ASP.NET, or a third-party SDK lives in Tier-C.

A package that another packable package `ProjectReferences` must itself be packable — otherwise the parent's nuspec lists a dependency that doesn't exist on the feed. The `Stratara.Publish.slnf` solution filter enforces which projects are packable.

## What lives where, in plain English

| If you want to… | Add a reference to… |
|---|---|
| Dispatch a command in-process | `Stratara.Mediator` |
| Dispatch a command async via outbox | `Stratara.Mediator` + `Stratara.Outbox.RabbitMQ` (or `Outbox.AzureServiceBus`) |
| Persist events to PostgreSQL | `Stratara.EventSourcing.EntityFrameworkCore` |
| Run projections on the read side | `Stratara.Projections` |
| Run sagas / process managers | `Stratara.Sagas` |
| Encrypt sensitive properties | `Stratara.Infrastructure` (brings the `[EncryptData]` infrastructure with it) |
| Plug in ASP.NET identity | `Stratara.Identity.AspNetCore` |
| Wire OpenTelemetry + Serilog | `Stratara.ServiceDefaults` (plus `.AspNetCore` for HTTP-host extras) |
| Just the interfaces, no impl | `Stratara.Abstractions` + `Stratara.Contracts` |

## Why this layout

- **Stable Tier-A surface.** Consumers can pin Tier-A interfaces and let Tier-B/C bump independently within a major version.
- **No consumer pollution.** Anything specific to a downstream consumer stays in the consumer — Stratara absorbs none of it.
- **Composable workers.** `Stratara.EventSourcing.WorkerDefaults` provides 6 named worker composites (CommandHandling, EventProjection, EventStreamHashing, OutboxHandling, SagaOrchestration, ServiceDefaults). Consumer hosts opt in à la carte.

## Event flow (at a glance)

```
Command → CommandOutboxDispatcher → RabbitMQ → CommandWorker → Handler
  → IEventSource → Events → WriteStore → EventBundle
  → ProjectionWorker → Projections → ReadStore
```

The full DI composition story is in **[DI Composition](../getting-started/di-composition.md)**.
