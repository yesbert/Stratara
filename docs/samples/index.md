# Samples

Five end-to-end runnable demos along a learning path. All samples share the same bank-account / money-transfer domain so you don't have to re-learn the problem space for each one.

| # | Sample | Concept | Lines (approx) | Read |
|---|---|---|---:|---|
| 1 | [CQRS Basics](01-cqrs-basics.md) | `IMediator` + `ICommand` / `IQuery` + handler discovery | ~200 | 5–10 min |
| 2 | [Event Sourced](02-event-sourced.md) | Event-sourced aggregate + projection (read/write separation) | ~250 | 10–15 min |
| 3 | [Outbox + Worker](03-outbox-worker.md) | Outbox + message bus + two background workers (async dispatch) | ~300 | 15–20 min |
| 4 | [Money-Transfer Saga](04-money-transfer-saga.md) | Saga / process manager — one command fans out into two via the outbox | ~330 | 15–20 min |
| 5 | [ASP.NET Core API](05-aspnetcore-api.md) | HTTP minimal-API endpoints → mediator wiring | ~250 | 10–15 min |

Samples 2–4 build conceptually on the one before; sample 5 is parallel to 1 and can be read at any point.

Each sample is **self-contained code** (no shared "Stratara.Sample.Common" project) — duplication between samples is intentional so each one reads from top to bottom without jumping to a shared library. The 5 samples are smoke-tested in CI via [`tests/Stratara.Samples.SmokeTests/`](https://github.com/yesbert/Stratara/tree/main/tests/Stratara.Samples.SmokeTests) — every release ships only after each sample's `stdout` has been asserted line-for-line.

## Running locally

```bash
dotnet run --project samples/Stratara.Sample.CqrsBasics
```
