# Sample 4 — Money-Transfer Saga

**Concept**: Saga / process manager. One `TransferCommand` fans out into a `WithdrawCommand` + `DepositCommand` via the outbox.

- **Code**: [`samples/Stratara.Sample.MoneyTransferSaga`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.MoneyTransferSaga)
- **Lines**: ~330
- **Read time**: 15–20 min
- **Prerequisite**: [Sample 3 — Outbox + Worker](03-outbox-worker.md).

## What you'll see

1. **`TransferCommand(from, to, amount)`** — the input. A saga, not a handler, reacts to it.
2. **`MoneyTransferSaga`** — implements `ISaga`. Validates the transfer (source-account balance), then **enqueues two commands** in one transaction: `WithdrawCommand(from, amount)` + `DepositCommand(to, amount)`.
3. The withdraw + deposit run **asynchronously, in parallel** through the outbox + command-worker. The saga doesn't wait for them.
4. **Rejection path** — if the saga sees the transfer is invalid (insufficient balance), it never enqueues the down-stream commands. The rejection happens **before fan-out**.

## Running

```bash
dotnet run --project samples/Stratara.Sample.MoneyTransferSaga
```

Expected output (abridged):

```
=== Stratara Money-Transfer Saga ===

--- Open two accounts via outbox (Alice $200, Bob $50) ---
  Alice: $200.00
  Bob:   $50.00

--- Transfer $75 from Alice to Bob (saga handler enqueues Withdraw + Deposit) ---
  Alice: $125.00
  Bob:   $125.00

--- Transfer $999 from Alice to Bob (should fail — saga validates before enqueueing) ---
  Rejected: Account {guid} has balance $125.00; cannot withdraw $999.00.

Done.
```

## What changed vs. Sample 3

| Sample 3 (one command → one handler) | Sample 4 (one command → saga → many commands) |
|---|---|
| Caller's `EnqueueAsync(WithdrawCommand)` directly hits the withdraw handler | Caller's `EnqueueAsync(TransferCommand)` hits a saga first; the saga fans out |
| No business rule "withdraw and deposit must agree" | Saga *is* that rule — validates before issuing the pair |
| Stratara's `ICommandHandler<T>` | Stratara's `ISaga` — handles **events**, not commands; emits commands |

## Common pitfalls

- **Sagas should not depend on at-most-once delivery.** If the bus replays a `Transferred` event, the saga's handler must be idempotent (e.g. keyed by `TransferId`).
- **Two-phase commit is not what Stratara provides.** A failed `WithdrawCommand` after `DepositCommand` succeeded is a compensation-saga problem — Stratara gives you the runtime; the compensation logic is yours.
