# Stratara.Sample.MoneyTransferSaga

Sample #4 of the learning path. Adds a **saga** on top of the outbox + worker chain from sample #3 — one `RequestMoneyTransferCommand` fans out into two follow-up commands (`Withdraw` + `Deposit`) that execute asynchronously on the worker side.

## What's new versus `OutboxWorker`

| Concern | `OutboxWorker` | `MoneyTransferSaga` |
|---|---|---|
| Commands per "business operation" | 1 | 1 incoming → 2 enqueued |
| Cross-aggregate invariants | none | source-balance check in the saga handler before enqueueing |
| Compensation | n/a | rejection happens pre-emptively (no withdraw → nothing to roll back) |

## The saga handler

The whole orchestration is in [`Sagas/RequestMoneyTransferCommand.cs`](Sagas/RequestMoneyTransferCommand.cs):

```csharp
public Task HandleAsync(RequestMoneyTransferCommand command, CancellationToken cancellationToken)
{
    var source = accounts.Get(command.SourceAccountId);
    if (command.Amount > source.Balance)
    {
        throw new InsufficientBalanceException(source.Id, source.Balance, command.Amount);
    }

    dispatcher.Enqueue(new WithdrawCommand(command.SourceAccountId, command.Amount));
    dispatcher.Enqueue(new DepositCommand(command.DestinationAccountId, command.Amount));
    return Task.CompletedTask;
}
```

The handler:
1. Reads the source account (a **read** inside a command handler — controversial in CQRS purism, justified here because the saga needs to fail fast).
2. Throws if the invariant is violated — no events leave the handler, the transfer never starts.
3. Otherwise enqueues both follow-up commands on the outbox. They run asynchronously through the same drain + worker chain from sample #3.

## What this sample is — and isn't

What it **is**: a *process manager*. One incoming command, multiple follow-up commands, a single pre-flight invariant check, no state held across calls.

What it **isn't**: an event-driven saga in the full Stratara sense. Stratara's `ISaga` is invoked by the saga worker for each `IEvent` flowing through the event bundle. A canonical money-transfer saga would:

1. App publishes `MoneyTransferRequestedEvent`.
2. Saga reacts to it → emits `WithdrawCommand`.
3. Worker handles withdraw → emits `AmountWithdrawnEvent`.
4. Saga reacts to `AmountWithdrawnEvent` with matching correlation id → emits `DepositCommand`.
5. Worker handles deposit → emits `AmountDepositedEvent`.
6. Saga reacts → marks transfer complete, optional `MoneyTransferCompletedEvent`.

The full event-driven choreography is conceptually the same fan-out, but spans multiple saga method invocations across event bundles instead of one synchronous handler. We use the simpler form here to keep the sample focused; the file count would roughly double with full choreography.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.MoneyTransferSaga
```

Expected:
- Alice opens with $200, Bob with $50.
- Transfer $75: Alice $125, Bob $125 (workers process both follow-up commands).
- Transfer $999: rejected pre-flight (`InsufficientBalanceException`).

## Where to go next

- **`Stratara.Sample.AspNetCoreApi`** — same dispatch / outbox surface, driven by HTTP endpoints.

## How this maps to the real Stratara

| Sample type | Real Stratara |
|---|---|
| `MoneyTransferSagaHandler` (ICommandHandler) | `ISaga` implementation in `Stratara.Sagas`, dispatched by `SagaHandler` per matching `IEvent` |
| Pre-flight read inside the handler | typically replaced by aggregate-rebuild via `IAggregationService` in a real saga |
| Outbox + worker chain | unchanged from sample #3 — Stratara saga emits commands via the same `ICommandOutboxDispatcher` |
