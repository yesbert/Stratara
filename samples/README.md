# Stratara Samples

Two **Hero Samples** show what makes Stratara different — tamper-evident event streams and tenant-aware encryption — in ~100 lines each. Five **Learning Path** samples walk the core CQRS / Event Sourcing / Outbox / Saga / ASP.NET wiring in order. Every sample is self-contained (no shared common project) and runs in under a second.

All seven share the same **bank-account / money-transfer** domain so you don't have to re-learn the problem space for each one.

## 🌟 Hero Samples

The why-Stratara demos. Open one, run it, see the point.

| Sample | What it proves |
|---|---|
| [`Stratara.Sample.TamperProof`](Stratara.Sample.TamperProof) | Hash-chained event streams catch direct-DB tampering at the next verifier pass. Same idea as Stratara's `EventStreamHashing` worker, distilled to in-memory code. |
| [`Stratara.Sample.Encryption`](Stratara.Sample.Encryption) | `[EncryptData]`-marked fields with tenant-bound AAD make cross-tenant decryption fail by AES-GCM — even with the correct master key. |

## 📚 Learning Path

End-to-end runnable demos in a teaching order. Each builds on the prior.

| # | Sample | Concept | Lines (approx) | Read |
|---|---|---|---:|---|
| 1 | [`Stratara.Sample.CqrsBasics`](Stratara.Sample.CqrsBasics) | `IMediator` + `ICommand` / `IQuery` + handler discovery | ~200 | 5–10 min |
| 2 | [`Stratara.Sample.EventSourced`](Stratara.Sample.EventSourced) | Event-sourced aggregate + projection (read/write separation) | ~250 | 10–15 min |
| 3 | [`Stratara.Sample.OutboxWorker`](Stratara.Sample.OutboxWorker) | Outbox + message bus + two background workers (async dispatch) | ~300 | 15–20 min |
| 4 | [`Stratara.Sample.MoneyTransferSaga`](Stratara.Sample.MoneyTransferSaga) | Saga / process manager — one command fans out into two via the outbox | ~330 | 15–20 min |
| 5 | [`Stratara.Sample.AspNetCoreApi`](Stratara.Sample.AspNetCoreApi) | HTTP minimal-API endpoints → mediator wiring | ~250 | 10–15 min |

Samples 2–4 build conceptually on the one before; sample 5 is parallel to 1 and can be read at any point.

Each sample is **self-contained code** — duplication between samples is intentional so each one reads from top to bottom without jumping to a shared library.

## Running

```bash
dotnet run --project samples/Stratara.Sample.TamperProof
```

## Building all samples

Samples are excluded from `Stratara.Publish.slnf` but smoke-tested in CI via [`tests/Stratara.Samples.SmokeTests/`](../tests/Stratara.Samples.SmokeTests) — an API change that breaks a sample fails CI rather than rotting silently. To build all samples locally:

```bash
for csproj in samples/Stratara.Sample.*/*.csproj; do
    dotnet build "$csproj" -c Release --nologo
done
```
