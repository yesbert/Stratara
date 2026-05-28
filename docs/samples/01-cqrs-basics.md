# Sample 1 — CQRS Basics

**Concept**: `IMediator` + `ICommand` / `IQuery` + handler discovery — the minimum viable Stratara app.

- **Code**: [`samples/Stratara.Sample.CqrsBasics`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.CqrsBasics)
- **Lines**: ~200
- **Read time**: 5–10 min
- **What it doesn't have**: event store, outbox, broker, persistence — everything is in-memory.

## What you'll see

1. **`OpenAccountCommand` / `DepositCommand` / `WithdrawCommand`** marked as `ICommand<Guid>` (open returns the new ID) and `ICommand` (deposit + withdraw are fire-and-forget).
2. **`GetBalanceQuery : IQuery<decimal>`** — read-only.
3. Handlers implement `IQueryHandler<TRequest, TResult>` (the unified interface used by both `ICommand<T>` and `IQuery<T>`).
4. **DI wiring** via `AddMediator()` + `AddCommandHandlersFromAssemblyContaining<Program>()` + `AddQueryHandlersFromAssemblyContaining<Program>()` — no per-handler registrations.
5. **`InsufficientBalanceException`** is thrown synchronously and bubbles back through `mediator.HandleAsync(…)` — the rejection path.

## Running

```bash
dotnet run --project samples/Stratara.Sample.CqrsBasics
```

Expected output:

```
=== Stratara CQRS Basics ===

--- Open account ---
  Opened {guid} with $100

--- Deposit $50 ---
  Balance after deposit: $150.00

--- Withdraw $75 ---
  Balance after withdraw: $75.00

--- Withdraw $999 (should fail) ---
  Rejected: Account {guid} has balance $75.00; cannot withdraw $999.00.

Done.
```

## What's new vs. [First Stratara App](../getting-started/first-stratara-app.md)

- Two more command types (`Deposit`, `Withdraw`) — shows the `ICommand` (no result) variant in action.
- The `InsufficientBalanceException` flow — how the mediator pipeline propagates domain exceptions back to the caller.

## What's missing (covered by later samples)

- **No event store** — once you want event sourcing, deposits become `AmountDeposited` events on a stream. See [Sample 2: Event Sourced](02-event-sourced.md).
- **No outbox** — the commands all run synchronously in the caller's thread. Async fan-out comes in [Sample 3: Outbox + Worker](03-outbox-worker.md).
