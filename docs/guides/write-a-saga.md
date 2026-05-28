# Write a Saga

A saga (a.k.a. process manager) reacts to events by issuing more commands. Stratara registers sagas via `AddSagasFromAssemblyContaining<T>()` + the `SagaOrchestrationWorker`.

## The interface

```csharp
public interface ISaga
{
    Task HandleAsync(IReadOnlyList<IEvent> relevantEvents, CancellationToken cancellationToken);
}
```

Mechanically identical to `IProjection`. The difference is semantic: a **projection** updates a read-model; a **saga** issues more commands.

## A minimal saga

```csharp
public sealed class TransferSaga(
    ICommandOutboxDispatcher dispatcher,
    IAccountQueryStore accounts) : ISaga
{
    public async Task HandleAsync(IReadOnlyList<IEvent> relevantEvents, CancellationToken ct)
    {
        foreach (var ev in relevantEvents)
        {
            if (ev is IEvent<TransferRequested> requested)
            {
                await HandleAsync(requested.Payload, ct);
            }
        }
    }

    private async Task HandleAsync(TransferRequested ev, CancellationToken ct)
    {
        var sourceBalance = await accounts.GetBalanceAsync(ev.FromAccountId, ct);
        if (sourceBalance < ev.Amount)
        {
            // Validation failed — saga emits no commands, transfer never happens
            return;
        }

        await dispatcher.EnqueueAsync(new WithdrawCommand(ev.FromAccountId, ev.Amount), ct);
        await dispatcher.EnqueueAsync(new DepositCommand(ev.ToAccountId, ev.Amount), ct);
    }
}
```

## Register

```csharp
services.AddSagasFromAssemblyContaining<TransferSaga>();
```

And on the worker host:

```csharp
builder.AddSagaWorkerServices();
```

## Idempotency

Sagas **must be idempotent** — at-least-once delivery means the bus can replay the same event after a broker reconnect. Use one of:

- **Idempotency key** on the down-stream commands (Stratara's `CommandEnvelope` carries an `IdempotencyKey`; consumers can dedup).
- **State tracking** in your own read-store (`HasTransferBeenStarted(transferId)` before enqueueing).

## Compensation is your job

Stratara does **not** provide a two-phase commit. If the `WithdrawCommand` succeeds and the `DepositCommand` fails (the destination account was closed mid-transfer), the saga's down-stream listener has to emit a compensating `RefundCommand` against the source account.

The pattern: the saga listens for both `WithdrawSucceeded` and `DepositFailed`. On `DepositFailed`, it issues `RefundCommand`. Stratara just gives you the wiring; the choreography is yours.

## Anti-patterns

- **Don't `await` commands issued via the outbox.** `dispatcher.EnqueueAsync(…)` returns as soon as the row is written. If you want a synchronous response, use `IMediator.HandleAsync(…)` directly (but you give up at-least-once delivery).
- **Don't query write-store state from the saga.** Query a projection or a read-store. The saga is a read-side actor that produces write-side effects — keep its reads on the read side.
