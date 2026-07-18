# Stratara.Shared

> **License:** [MIT](../../LICENSE).

Umbrella of shared utilities for the Stratara framework. Re-exports the Tier-A/B stack (Abstractions, Contracts, Diagnostics, Domain, Resilience, SessionContext) so consumers can pull one package and reach every common type.

## Contents

- Source-generated `Logger*Extensions` for command flows. The outbox, messaging, saga, and projection surfaces have since moved to the packages that own them (`Stratara.Outbox.RabbitMQ`, `Stratara.Sagas`, `Stratara.Projections`) — they keep the `Stratara.Shared.Diagnostics.Extensions` namespace, which is why they can look like they still live here.
- Domain-event helpers + merge primitives used across the framework.
- Re-export of every Tier-A/B Stratara public type via project-reference fan-out.

## Quick reference

```csharp
// One package reference reaches every Tier-A/B public type
using Stratara.Abstractions.Mediator;          // ICommand, IQuery, IMediator
using Stratara.Contracts.Session;              // SessionContext
using Stratara.Diagnostics;                    // ApplicationDiagnostics

// Source-generated logger extensions provided by Shared
logger.LogCommandWorkerStarted();
```

## Dependencies

Transitively depends on every Tier-A/B package: `Stratara.Abstractions`, `Stratara.Contracts`, `Stratara.Diagnostics`, `Stratara.Domain`, `Stratara.Resilience`, `Stratara.Sessions`.
