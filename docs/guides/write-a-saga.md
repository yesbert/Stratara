# Write a Saga

A saga (a.k.a. process manager) reacts to events by issuing more commands. Stratara registers sagas via `AddSagasFromAssemblyContaining<T>()` + the `SagaOrchestrationWorker`.

## The contract

`ISaga` (`Stratara.Sagas.Abstractions`) is an **empty marker**, exactly like `IProjection`. The
runtime reflects over your class for `HandleAsync(IEvent<TEvent>, CancellationToken)` methods and
routes matching events to them. The difference from a projection is semantic: a projection updates a
read model; a saga issues more commands.

## A minimal saga

```csharp
using JetBrains.Annotations;
using Stratara.Abstractions.EventSourcing;
using Stratara.Sagas.Abstractions;

public sealed class TransferSaga(
    ICommandOutboxDispatcher dispatcher,
    IAccountQueryStore accounts) : ISaga
{
    [UsedImplicitly]
    private async Task HandleAsync(IEvent<TransferRequested> @event, CancellationToken ct)
    {
        var transfer = @event.Data;
        var sourceBalance = await accounts.GetBalanceAsync(transfer.FromAccountId, ct);
        if (sourceBalance < transfer.Amount)
        {
            return;   // validation failed — the saga emits no commands, the transfer never happens
        }

        await dispatcher.EnqueueCommandAsync(new WithdrawCommand(transfer.FromAccountId, transfer.Amount), ct);
        await dispatcher.EnqueueCommandAsync(new DepositCommand(transfer.ToAccountId, transfer.Amount), ct);
    }
}
```

Write one `HandleAsync(IEvent<TEvent>, CancellationToken)` per event you react to — the payload is
`@event.Data`. Handlers may be private; mark them `[UsedImplicitly]` so analyzers don't flag them.
Commands go out through `ICommandOutboxDispatcher.EnqueueCommandAsync`.

## Register

```csharp
builder.AddSagaWorkerServices();                            // the worker host composite
builder.Services.AddSagasFromAssemblyContaining<TransferSaga>();
```

`AddSagaWorkerServices()` (from `Stratara.EventSourcing.WorkerDefaults`) brings the hosted
`SagaWorker` and its dependencies; `AddSagasFromAssemblyContaining<T>()` registers your sagas.

## Idempotency

Sagas **must be idempotent** — at-least-once delivery means the bus can replay the same event after a broker reconnect. Because a redelivery re-runs `HandleAsync`, guard the enqueue:

- **State tracking** in your own read-store — `HasTransferBeenStarted(transferId)` before enqueueing, so a replay is a no-op.
- **Deterministic command identity** — derive the down-stream command's own key from the source event so a duplicate enqueue collapses at the handler rather than moving money twice.

## Compensation is your job

Stratara does **not** provide a two-phase commit. If the `WithdrawCommand` succeeds and the `DepositCommand` fails (the destination account was closed mid-transfer), the saga's down-stream listener has to emit a compensating `RefundCommand` against the source account.

The pattern: the saga listens for both `WithdrawSucceeded` and `DepositFailed`. On `DepositFailed`, it issues `RefundCommand`. Stratara just gives you the wiring; the choreography is yours.

## Anti-patterns

- **Don't expect a result from an outbox command.** `dispatcher.EnqueueCommandAsync(…)` returns once the row is written, not once the command runs — you get the envelope id, not the outcome. If you need a synchronous result, dispatch through `IMediator.HandleAsync(…)` instead (but you give up the outbox's at-least-once delivery).
- **Don't query write-store state from the saga.** Query a projection or a read-store. The saga is a read-side actor that produces write-side effects — keep its reads on the read side.
