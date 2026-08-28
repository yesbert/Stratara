# Sample 4 — Money-Transfer Saga

> **Derived page.** The behaviour described here is specified by the `sagas` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

> **This saga is written out by hand.** The sample declares its own saga type, outbox and bus so it
> reads top to bottom without a database or a broker. The framework ships the real thing — implement
> `ISaga` from `Stratara.Sagas` and register it with `AddSagasFromAssemblyContaining<T>()`. See
> [Write a Saga](../guides/write-a-saga.md) for the wiring a host actually uses.


**Concept**: Process manager. One `RequestMoneyTransferCommand` fans out into a `WithdrawCommand` + `DepositCommand` via the outbox.

- **Code**: [`samples/Stratara.Sample.MoneyTransferSaga`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.MoneyTransferSaga)
- **Lines**: ~340
- **Read time**: 15–20 min
- **Prerequisite**: [Sample 3 — Outbox + Worker](03-outbox-worker.md).

## What you'll see

1. **`RequestMoneyTransferCommand(SourceAccountId, DestinationAccountId, Amount)`** — the input, an ordinary `ICommand`.
2. **`MoneyTransferSagaHandler : ICommandHandler<RequestMoneyTransferCommand>`** — checks the source-account balance, then **enqueues two commands**: `WithdrawCommand(from, amount)` + `DepositCommand(to, amount)`.
3. The withdraw + deposit run **asynchronously** through the outbox + command-worker. The handler doesn't wait for them.
4. **Rejection path** — an insufficient balance throws `InsufficientBalanceException` and the down-stream commands are never enqueued. The rejection happens **before fan-out**.

> **This is a process manager, not an event-driven saga.** The sample deliberately models the fan-out
> with a plain command handler and does not reference `Stratara.Sagas` at all. The framework's
> `ISaga` is invoked by the saga worker for each `IEvent` in an event bundle — a canonical
> money-transfer saga would react to `MoneyTransferRequestedEvent`, then to `AmountWithdrawnEvent`,
> across several invocations. Same fan-out, spread over event bundles instead of one synchronous
> handler; the sample's README walks through that fuller choreography.

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

| Sample 3 (one command → one handler) | Sample 4 (one command → process manager → many commands) |
|---|---|
| `dispatcher.Enqueue(new WithdrawCommand(...))` directly hits the withdraw handler | `dispatcher.Enqueue(new RequestMoneyTransferCommand(...))` hits `MoneyTransferSagaHandler` first; it fans out |
| No business rule "withdraw and deposit must agree" | The handler *is* that rule — validates before issuing the pair |
| A handler that mutates one aggregate | A handler that issues further commands and holds no state across calls |

## Common pitfalls

- **Don't depend on at-most-once delivery.** The outbox redelivers, so a fan-out handler must be idempotent — enqueueing the pair twice must not move the money twice. Key the work by a transfer id.
- **Two-phase commit is not what Stratara provides.** A failed `WithdrawCommand` after `DepositCommand` succeeded is a compensation-saga problem — Stratara gives you the runtime; the compensation logic is yours.
