---
_layout: landing
---

# Stratara

**Application-agnostic CQRS, Event Sourcing, Mediator and supporting infrastructure framework for .NET 10.**

Stratara is a family of 20 NuGet packages that provides the building blocks for write/read-separated, event-sourced, multi-tenant applications — without dictating the shape of your domain. Stratara handles the mechanics (mediator dispatch, event stores, projections, sagas, outbox-driven async messaging, identity wiring, observability defaults) so you can focus on your aggregates and use-cases.

## Where to start

- **[What is Stratara](overview/what-is-stratara.md)** — the 5-minute pitch + which problems it solves.
- **[Architecture at a glance](overview/architecture-at-a-glance.md)** — Tier-A / Tier-B / Tier-C package layout.
- **[Glossary](overview/glossary.md)** — Mediator, Command, Query, Aggregate, Outbox, Saga, Projection — in Stratara's vocabulary.
- **[First Stratara App](getting-started/first-stratara-app.md)** — a 30-line walkthrough.
- **[Samples](samples/index.md)** — five end-to-end runnable demos along a learning path (CQRS → Event Sourcing → Outbox → Saga → ASP.NET).
- **[API Reference](reference/index.md)** — auto-generated from XML docs across every public type.

## Install

```bash
dotnet add package Stratara.Mediator
dotnet add package Stratara.Abstractions
```

A typical host wires Stratara through one of the umbrella `Add*WorkerServices` extensions; see [getting started](getting-started/di-composition.md) for the full DI composition.

## Versioning

Stratara ships in lockstep — all 20 packages share the same `<VersionPrefix>`. See `CHANGELOG.md` for release notes.
