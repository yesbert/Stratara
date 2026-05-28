# Sample 2 — Event Sourced

**Concept**: Event-sourced aggregate + projection (read/write separation) — what changes when you replace the in-memory repository with an event store.

- **Code**: [`samples/Stratara.Sample.EventSourced`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.EventSourced)
- **Lines**: ~250
- **Read time**: 10–15 min
- **Prerequisite**: [Sample 1 — CQRS Basics](01-cqrs-basics.md).

## What you'll see

1. **Domain events** — `AccountOpened`, `AmountDeposited`, `AmountWithdrawn` as immutable `sealed record`s.
2. **`Account` aggregate** with **public `set`** on properties (snapshot-deserialize-friendly) and `Apply(…)` methods that apply each event type.
3. **`InMemoryEventStore`** — the sample's stand-in for `Stratara.EventSourcing.EntityFrameworkCore`. Same semantics, simpler dependencies.
4. **`AccountBalanceProjection`** — a read-model that re-builds itself from the event stream.
5. **Write-side invariant check** — `Withdraw $999` rejection is computed by **rebuilding the aggregate from events** to compute the current balance, not by reading the projection.

## Running

```bash
dotnet run --project samples/Stratara.Sample.EventSourced
```

Expected output (abridged):

```
=== Stratara Event-Sourced ===

--- Open account ---
  Opened {guid} with $100

--- Deposit $50 + Deposit $25 + Withdraw $40 ---

--- Read-side via projection ---
  Alice's balance: $135.00

--- Withdraw $999 (should fail — write-side rebuilds aggregate from events to check invariant) ---
  Rejected: Account {guid} has balance $135.00; cannot withdraw $999.00.

--- Underlying event stream (this is what's persisted) ---
  AccountOpened { … }
  AmountDeposited { … Amount = 50 … }
  AmountDeposited { … Amount = 25 … }
  AmountWithdrawn { … Amount = 40 … }
```

## What changed vs. Sample 1

| Sample 1 (CRUD) | Sample 2 (Event Sourced) |
|---|---|
| `account.Deposit(50)` mutates in memory | Appends `AmountDeposited(50)` to the stream |
| `repo.Get(id).Balance` reads current state directly | `projection.Get(id).Balance` reads a derived read-model |
| Invariant check reads `account.Balance` | Invariant check **rebuilds the aggregate** from all events first |
| 1 source of truth (the `Account` object) | 1 source of truth (the **event stream**); aggregate + projection both derive from it |

## What's missing (covered by later samples)

- **No async dispatch** — the event store is consumed synchronously inside the command handler. Async fan-out to projections via the outbox is [Sample 3](03-outbox-worker.md).
- **No saga** — there's no second command issued in response to the first. [Sample 4](04-money-transfer-saga.md) shows fan-out via a saga.
