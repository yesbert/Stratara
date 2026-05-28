# Stratara.Shared

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Umbrella of shared utilities for the Stratara framework. Re-exports the Tier-A/B stack (Abstractions, Contracts, Diagnostics, Domain, Resilience, SessionContext) so consumers can pull one package and reach every common type.

## Contents

- Source-generated `Logger*Extensions` for outbox, saga, projection, messaging, and command flows (kept in Shared until each subdomain extracts to its own Tier-C package).
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
