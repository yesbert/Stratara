---
_layout: landing
---

# Stratara

**CQRS and Event Sourcing for .NET — with tamper-evident streams and tenant-aware encryption built in.**

Stratara is the integrated CQRS, Event Sourcing, and audit stack you'd otherwise compose yourself from three or four libraries. Mediator, outbox, event store, sagas, projections, and identity — all wired together, lockstep-versioned across 20 NuGet packages for .NET 10. Opt in à la carte.

## Why Stratara

🔒 **[Tamper-Evident by Design](concepts/tamper-evident-streams.md)** — Every event stream is hash-chained. Manipulate a row directly in Postgres, and the next background-worker pass raises `EventStreamCorrupted` at the exact sequence number where the chain breaks. Audit-grade integrity, not a "trust the DBA" promise.

🛡️ **[Tenant-Aware Encryption](concepts/tenant-aware-encryption.md)** — `[EncryptData]` fields are sealed with AES-GCM and an authentication tag bound to the tenant id as Associated Data. A row leaked from one tenant cannot be decrypted in another tenant's session — *even with the correct master key*.

🧩 **Integrated, not Assembled** — Mediator + Outbox + Event Store + Sagas + Projections + Identity, lockstep-versioned across 20 packages. One `<VersionPrefix>` bump moves everything together. No multi-library composition tax.

## Where to start

- **[Concepts](concepts/index.md)** — the three load-bearing ideas. Read these first.
- **[Hero Samples](samples/index.md#hero-samples)** — TamperProof and Encryption, runnable in under a second.
- **[What is Stratara](overview/what-is-stratara.md)** — the 5-minute pitch, who it's for, and who it's not for.
- **[Architecture at a glance](overview/architecture-at-a-glance.md)** — tier layout, package boundaries, dependency direction.
- **[First Stratara App](getting-started/first-stratara-app.md)** — a 30-line walkthrough.
- **[Learning-path Samples](samples/index.md)** — five end-to-end demos along a CQRS → Event Sourcing → Outbox → Saga → ASP.NET progression.
- **[API Reference](reference/index.md)** — auto-generated from XML docs across every public type.

## Install

```bash
dotnet add package Stratara.Mediator
dotnet add package Stratara.Abstractions
```

A typical host wires Stratara through one of the umbrella `Add*WorkerServices` extensions; see [getting started](getting-started/di-composition.md) for the full DI composition.

## Versioning

Stratara ships in lockstep — all 20 packages share the same `<VersionPrefix>`. See `CHANGELOG.md` for release notes.
