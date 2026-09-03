# Packages

> **Derived page.** The behaviour described here is specified by the `package-distribution` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Stratara ships as **25 NuGet packages at one lockstep version** — every package below carries the
same `<VersionPrefix>`, bumped together, the way the `Microsoft.Extensions.*` family does. Take one
package or take twenty; they never disagree about which version of each other they expect.

## Tier layout

A Tier-N package references only Tier-N or lower. Tier-A has no inbound dependency from B or C, so
a consumer can adopt the contracts without any infrastructure.

| Tier | Package | Purpose |
|---|---|---|
| A | `Stratara.Abstractions` | Contract interfaces + POCO records (no implementation) |
| A | `Stratara.Contracts` | Wire-level POCO contracts |
| A | `Stratara.Diagnostics` | `ActivitySource` / `Meter` / log-event-ID schema |
| A | `Stratara.Resilience` | Polly named pipelines |
| B | `Stratara.Sessions` | Actor / Subject session model + ASP.NET middleware |
| B | `Stratara.Mediator` | In-process mediator + pipeline behaviors |
| B | `Stratara.Domain` | Tenant aggregate + lifecycle events |
| B | `Stratara.Shared` | Umbrella re-export of A/B abstractions + source-generated logger extensions |
| B | `Stratara.ServiceDefaults` | OpenTelemetry + Serilog defaults |
| C | `Stratara.EventSourcing.EntityFrameworkCore` | Write / read / identity stores on PostgreSQL |
| C | `Stratara.EventSourcing.Pipeline.CommandAudit` | Command-audit pipeline behavior |
| C | `Stratara.Validation` | Vendor-neutral `IValidator<T>` + validation pipeline behavior |
| C | `Stratara.EventSourcing.WorkerDefaults` | Worker-host wiring composites |
| C | `Stratara.Projections` | Projection runtime |
| C | `Stratara.Sagas` | Saga runtime |
| C | `Stratara.Security` | Key store (KEK-wrapped versioned DEKs) + AES-GCM envelope encryption |
| C | `Stratara.Outbox.RabbitMQ` | Outbox + RabbitMQ-backed `IMessageBus` |
| C | `Stratara.Outbox.AzureServiceBus` | Outbox + Azure Service Bus-backed `IMessageBus` |
| C | `Stratara.Infrastructure` | Cross-cutting infrastructure glue |
| C | `Stratara.Identity.Core` | Channel-agnostic identity primitives |
| C | `Stratara.Identity.AspNetCore` | ASP.NET Core identity wiring: sign-in manager wrapper, membership tenant-claim bridge, i18n, email-sender stub |
| C | `Stratara.Identity.EntityFrameworkCore` | Identity directory: user↔tenant membership, membership-backed authorization (roles + permissions), scoped settings store |
| C | `Stratara.ServiceDefaults.AspNetCore` | ASP.NET health checks + request OpenTelemetry |
| — | `Stratara.Testing` | Test doubles (in-memory key store / message bus / session) + given/when/then aggregate harness — reference from test projects only |
| — | `Stratara.Testing.EntityFrameworkCore` | The real event-sourcing write stack on in-memory SQLite (`EventStoreTestHost`) — reference from test projects only |

## Which packages for which door

| You want | Reference |
|---|---|
| A mediator, nothing else | `Stratara.Mediator` |
| Request validation as a pipeline behavior | `+ Stratara.Validation` |
| Event sourcing on PostgreSQL with outbox, projections and sagas over RabbitMQ | `Stratara.EventSourcing.WorkerDefaults` (pulls the stack transitively) `+ Stratara.Abstractions` `+ Stratara.Sessions` |
| The same over Azure Service Bus | swap `Stratara.Outbox.RabbitMQ` for `Stratara.Outbox.AzureServiceBus` |
| Field-level encryption and crypto-shredding without the event store | `Stratara.Security` |
| Tenant membership, permissions, API keys | `Stratara.Identity.EntityFrameworkCore` `+ Stratara.Identity.AspNetCore` |
| Tests | `Stratara.Testing`, `Stratara.Testing.EntityFrameworkCore` (test projects only) |

## Versioning

A `v*` tag publishes the whole family to nuget.org; nothing publishes on a merge. A tag may name a
prerelease (`v4.0.0-preview.1`), which reaches only those who ask for one with `--prerelease`.
Release notes per version are in the repository's `CHANGELOG.md` and on the
[releases page](https://github.com/yesbert/Stratara/releases).
