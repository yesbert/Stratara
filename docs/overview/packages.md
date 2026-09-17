---
title: "Packages"
description: "Every Stratara package with what it does and what it pulls in, plus the combinations that cover the common hosts. One lockstep version across the family."
---

# Packages

> **Derived page.** The behaviour described here is specified by the `package-distribution` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Stratara ships as **28 NuGet packages at one lockstep version** — every package below carries the
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
| C | `Stratara.Orleans` | The Orleans execution model: commands, projections, sagas, timers and singleton work as virtual actors |
| C | `Stratara.Orleans.EntityFrameworkCore` | The execution model's persistence: commit-order readers, checkpoint store, intent store, reset |
| — | `Stratara.Testing` | Test doubles (in-memory key store / message bus / session) + given/when/then aggregate harness — reference from test projects only |
| — | `Stratara.Testing.EntityFrameworkCore` | The real event-sourcing write stack on in-memory SQLite (`EventStoreTestHost`) — reference from test projects only |
| — | `Stratara.Testing.Orleans` | The Orleans execution model in the test's process — one silo, in-memory reminders and directory, SQLite store (`ExecutionModelTestHost`) — reference from test projects only |

## Which packages for which door

| You want | Reference |
|---|---|
| A mediator, nothing else | `Stratara.Mediator` |
| Request validation as a pipeline behavior | `+ Stratara.Validation` |
| Event sourcing on PostgreSQL with outbox, projections and sagas over RabbitMQ | `Stratara.EventSourcing.WorkerDefaults` (pulls the stack transitively) `+ Stratara.Abstractions` `+ Stratara.Sessions` |
| The same over Azure Service Bus | swap `Stratara.Outbox.RabbitMQ` for `Stratara.Outbox.AzureServiceBus` |
| Commands, projections, sagas and timers on an Orleans cluster | `+ Stratara.Orleans` `+ Stratara.Orleans.EntityFrameworkCore` — see [Choose an Execution Model](../getting-started/choose-an-execution-model.md) |
| Field-level encryption and crypto-shredding without the event store | `Stratara.Security` |
| Tenant membership, permissions, API keys | `Stratara.Identity.EntityFrameworkCore` `+ Stratara.Identity.AspNetCore` |
| Tests | `Stratara.Testing`, `Stratara.Testing.EntityFrameworkCore`, `Stratara.Testing.Orleans` for the execution model (test projects only) |

## Debugging into the framework

Every published package lets your debugger step from your own code into Stratara's source, and the
source you see is the source the package was built from, at that exact commit.

- **Symbols ship with every package.** Each package is published with a symbol package (`.snupkg`)
  pushed to the NuGet.org symbol server in the same step as the package itself.
- **The symbols carry SourceLink.** They point at the public GitHub repository at the commit the
  release was built from, so the debugger fetches the matching file rather than whatever is on
  `main` today. Files generated during the build are embedded in the symbols.
- **Builds are deterministic.** Releases are built as continuous-integration builds with
  deterministic compilation, so building the same commit twice produces identical outputs and the
  symbols always match the binaries you downloaded.

To use this, turn off "Just My Code" and enable the NuGet.org symbol server in your debugger. In
Visual Studio both are under *Tools → Options → Debugging*. In VS Code with the C# extension, set
them in the launch configuration:

```jsonc
{
  "type": "coreclr",
  "request": "launch",
  "justMyCode": false,
  "symbolOptions": { "searchNuGetOrgSymbolServer": true }
}
```

A local build from source is marked as a `dev` prerelease and is not a continuous-integration build.
The deterministic-output guarantee is about the published packages.

## Versioning

A `v*` tag publishes the whole family to nuget.org; nothing publishes on a merge. A tag may name a
prerelease (`v4.0.0-preview.1`), which reaches only those who ask for one with `--prerelease`.
Release notes per version are in the repository's `CHANGELOG.md` and on the
[releases page](https://github.com/yesbert/Stratara/releases).
