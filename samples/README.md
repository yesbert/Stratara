# Stratara Samples

Each sample is a self-contained console (or web) project that demonstrates one Stratara concept end-to-end. They are intentionally **not** packed as NuGets and not part of `Stratara.Publish.slnf` — they exist as a learning path and a smoke-test that the Stratara public API still composes cleanly.

The samples share the same **bank-account / money-transfer** domain so you don't have to re-learn the problem space for each one.

## Learning path

| # | Sample | Concept | Lines (approx) | Read |
|---|---|---|---|---|
| 1 | [`Stratara.Sample.CqrsBasics`](Stratara.Sample.CqrsBasics) | `IMediator` + `ICommand` / `IQuery` + handler discovery | ~200 | 5–10 min |
| 2 | [`Stratara.Sample.EventSourced`](Stratara.Sample.EventSourced) | Event-sourced aggregate + projection (read/write separation) | ~250 | 10–15 min |
| 3 | [`Stratara.Sample.OutboxWorker`](Stratara.Sample.OutboxWorker) | Outbox + message bus + two background workers (async dispatch) | ~300 | 15–20 min |
| 4 | [`Stratara.Sample.MoneyTransferSaga`](Stratara.Sample.MoneyTransferSaga) | Saga / process manager — one command fans out into two via the outbox | ~330 | 15–20 min |
| 5 | [`Stratara.Sample.AspNetCoreApi`](Stratara.Sample.AspNetCoreApi) | HTTP minimal-API endpoints → mediator wiring | ~250 | 10–15 min |

Samples 2–4 build conceptually on the one before; sample 5 is parallel to 1 and can be read at any point. Each sample is **self-contained code** (no shared "Stratara.Sample.Common" project) — duplication between samples is intentional so each one reads from top to bottom without jumping to a shared library.

## Running

```bash
dotnet run --project samples/Stratara.Sample.CqrsBasics
```

## Building all samples

Samples are excluded from `Stratara.Publish.slnf` but included in the CI unit-tests pipeline as a no-test build step, so an API change that breaks a sample fails CI rather than rotting silently. To build all samples locally:

```bash
for csproj in samples/Stratara.Sample.*/*.csproj; do
    dotnet build "$csproj" -c Release --nologo
done
```
