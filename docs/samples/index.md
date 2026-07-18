# Samples

Runnable demos. The two **Hero Samples** show what makes Stratara different — tamper-evident streams and tenant-aware encryption — with no dependencies at all: no database, no DI container. The five **Learning Path** samples walk through the core CQRS / Event Sourcing / Outbox / Saga / ASP.NET wiring in order. A **Pipeline Behaviors** sample covers request validation, and two **Identity & Access** samples cover sign-in and the identity directory.

The hero + learning-path samples share the same bank-account / money-transfer domain so you don't have to re-learn the problem space for each one.

## Hero Samples

The *why-Stratara* demos. Self-contained, zero-dependency, designed to make the point in under a minute.

| Sample | Concept | Read |
|---|---|---|
| [TamperProof](hero-tamper-proof.md) | Hash-chained event streams catch direct-DB tampering | 5–10 min |
| [Encryption](hero-encryption.md) | `[EncryptData]` with tenant-bound AAD prevents cross-tenant decryption | 5–10 min |

## Learning Path

End-to-end runnable demos along a CQRS-→-Event-Sourcing-→-Saga progression. Each one builds on the prior.

| # | Sample | Concept | Lines (approx) | Read |
|---|---|---|---:|---|
| 1 | [CQRS Basics](01-cqrs-basics.md) | `IMediator` + `ICommand` / `IQuery` + handler discovery | ~200 | 5–10 min |
| 2 | [Event Sourced](02-event-sourced.md) | Event-sourced aggregate + projection (read/write separation) | ~250 | 10–15 min |
| 3 | [Outbox + Worker](03-outbox-worker.md) | Outbox + message bus + two background workers (async dispatch) | ~300 | 15–20 min |
| 4 | [Money-Transfer Saga](04-money-transfer-saga.md) | Saga / process manager — one command fans out into two via the outbox | ~330 | 15–20 min |
| 5 | [ASP.NET Core API](05-aspnetcore-api.md) | HTTP minimal-API endpoints → mediator wiring | ~250 | 10–15 min |

Samples 2–4 build conceptually on the one before; sample 5 is parallel to 1 and can be read at any point.

## Pipeline Behaviors

Cross-cutting mediator behaviors that run before the handler. Self-contained, with a small user-registration command.

| Sample | Concept | Read |
|---|---|---|
| [Validation](06-validation.md) | `IValidator<T>` as a mediator pipeline behavior — valid, warning-only (still handled), and invalid (blocked) | 5–10 min |

## Identity & Access

Two halves of the same story: how a caller is authenticated, and what that identity may then do.
They use their own small tenant/simulation domain rather than the bank-account one.

| Sample | Concept | Read |
|---|---|---|
| [Identity](07-identity.md) | External OpenID Connect sign-in + hardened JIT provisioning, API keys / PATs, and the auth-scheme selector routing all three | 10–15 min |
| [Identity Directory](08-identity-directory.md) | Tenant membership (roles scoped per membership), `[RequirePermission]` at the mediator, and the scoped-settings fallback chain | 10–15 min |

Each sample is **self-contained code** (no shared "Stratara.Sample.Common" project) — duplication between samples is intentional so each one reads from top to bottom without jumping to a shared library. Every sample is smoke-tested in CI via [`tests/Stratara.Samples.SmokeTests/`](https://github.com/yesbert/Stratara/tree/main/tests/Stratara.Samples.SmokeTests) — releases ship only after each sample's `stdout` has been asserted line-for-line.

## Running locally

```bash
dotnet run --project samples/Stratara.Sample.TamperProof
dotnet run --project samples/Stratara.Sample.Encryption
dotnet run --project samples/Stratara.Sample.CqrsBasics
```
