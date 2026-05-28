# Stratara.Contracts

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Wire-level POCO contracts shared by every Stratara package. Pure data records — no runtime, no DI, no infrastructure deps. Safe to reference from any layer.

## Contents

- `Messages/EventMessage`, `EventBundle`, `CommandEnvelope` — the cross-process envelope shapes that messaging adapters (`Stratara.Outbox.RabbitMQ` etc.) serialise on and off the bus.
- `Requests/PagedRequest` — shared pagination + sort record used by query handlers across the family.
- `Session/SessionContext` — public data shape for actor/subject identity, correlation, causation, and connection routing. The corresponding service abstractions live in `Stratara.Sessions`.

## Quick reference

```csharp
// Build a CommandEnvelope outside the framework (rare, but supported)
var envelope = new CommandEnvelope(
    Id: Guid.CreateVersion7(),
    CommandJson: commandJson,
    CommandTypeName: typeof(MyCommand).AssemblyQualifiedName!,
    SessionContextJson: sessionContextJson);
```

## Dependencies

None. Contracts is the lowest tier in the Stratara dependency graph.
