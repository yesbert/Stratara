# Stratara.Sample.CqrsBasics

The first sample in the Stratara learning path. Shows the **Mediator** wire-up — `ICommand`, `ICommand<TResult>`, `IQuery<TResult>`, and their handlers — against a tiny in-memory bank-account domain. No event sourcing, no outbox, no saga; those are later samples.

## What to look at, in order

1. **`Program.cs`** — the entry point. Reads top-down as a sequential script: DI wiring → open scope → 4 mediator calls. Demonstrates `IMediator.HandleAsync<TResult>` (for `ICommand<Guid>` and `IQuery<decimal>`) and the no-result overload (for `ICommand`).

2. **`Domain/Account.cs`** — a plain mutable aggregate. The whole sample's "state" lives in `Balance`. `Withdraw` throws `InsufficientBalanceException` to demonstrate that domain invariants live on the aggregate, not the handler.

3. **`Commands/`** — three commands:
   - `OpenAccountCommand : ICommand<Guid>` — returns the new account id synchronously.
   - `DepositCommand : ICommand` — no result.
   - `WithdrawCommand : ICommand` — no result; throws on overdraft.

   Each handler is in the same file as its command (one logical concept per file).

4. **`Queries/GetBalanceQuery.cs`** — `IQuery<decimal>`. Side-effect-free read.

5. **`Infrastructure/InMemoryAccountRepository.cs`** — a `ConcurrentDictionary` standing in for the real write-store. In production this would be the EF Core write-store, but the mediator surface stays the same.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.CqrsBasics
```

Expected output: account opens with $100, deposit to $150, withdraw to $75, then an over-budget withdraw is rejected via the domain exception.

## Wire-up cheat sheet

```csharp
services.AddSingleton(TracerProvider.Default.GetTracer("Your.App"));  // Mediator depends on OTel Tracer

services
    .AddMediator()
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();
```

`AddCommandHandlersFromAssemblyContaining<T>` registers every concrete `ICommandHandler<>` in the assembly. `AddQueryHandlersFromAssemblyContaining<T>` registers every concrete `IQueryHandler<,>` — which also covers `ICommand<TResult>` handlers, since they share `IRequest<TResult>` with queries.

## Where to go next

- **`Stratara.Sample.EventSourced`** — replace the in-memory repository with an `IEventSource` and add a projection.
- **`Stratara.Sample.OutboxWorker`** — push `WithdrawCommand` through an outbox + worker instead of in-process.
- **`Stratara.Sample.MoneyTransferSaga`** — compose `Withdraw` + `Deposit` across two accounts as a saga.
- **`Stratara.Sample.AspNetCoreApi`** — wire HTTP endpoints to the same mediator.
