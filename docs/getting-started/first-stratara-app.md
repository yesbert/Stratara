# First Stratara App

A 30-line console app that wires the mediator, dispatches a command, and queries the result. No event store, no broker — just `Stratara.Mediator` running in-process. The full code is in `samples/Stratara.Sample.CqrsBasics`; this walkthrough explains it block by block.

## 1. Project setup

```bash
dotnet new console -o HelloStratara
cd HelloStratara
dotnet add package Stratara.Mediator
dotnet add package Stratara.Abstractions
```

## 2. Define your contracts

```csharp
using Stratara.Abstractions.Mediator;

public sealed record OpenAccountCommand(string OwnerName, decimal InitialBalance) : ICommand<Guid>;
public sealed record GetBalanceQuery(Guid AccountId) : IQuery<decimal>;
```

- `ICommand<Guid>` — a command that returns the new account's id.
- `IQuery<decimal>` — a read-only request that returns a balance.

## 3. Write a domain primitive

```csharp
public sealed class Account(Guid id, string ownerName, decimal initialBalance)
{
    public Guid Id { get; } = id;
    public string OwnerName { get; } = ownerName;
    public decimal Balance { get; private set; } = initialBalance;

    public void Deposit(decimal amount) => Balance += amount;
}
```

No event sourcing here — that's [sample 2](../samples/02-event-sourced.md). This is the simplest in-memory aggregate.

## 4. Write the handlers

```csharp
public sealed class OpenAccountHandler(InMemoryAccountRepository repo) : IQueryHandler<OpenAccountCommand, Guid>
{
    public Task<Guid> HandleAsync(OpenAccountCommand cmd, CancellationToken ct)
    {
        var account = new Account(Guid.NewGuid(), cmd.OwnerName, cmd.InitialBalance);
        repo.Save(account);
        return Task.FromResult(account.Id);
    }
}

public sealed class GetBalanceHandler(InMemoryAccountRepository repo) : IQueryHandler<GetBalanceQuery, decimal>
{
    public Task<decimal> HandleAsync(GetBalanceQuery q, CancellationToken ct) =>
        Task.FromResult(repo.Get(q.AccountId).Balance);
}
```

Note: `IQueryHandler<TRequest, TResult>` handles **both** `ICommand<T>` and `IQuery<T>` — they share the underlying `IRequest<T>` marker. Stratara picks one or the other based on which marker you declared.

## 5. Wire DI + dispatch

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<InMemoryAccountRepository>();
builder.Services
    .AddMediator()
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

var accountId = await mediator.HandleAsync(new OpenAccountCommand("Alice", 100m));
var balance = await mediator.HandleAsync(new GetBalanceQuery(accountId));
Console.WriteLine($"Account {accountId}: {balance:C}");
```

Three things to notice:

1. `AddMediator()` registers `IMediator` + the default pipeline behaviors.
2. `AddCommandHandlersFromAssemblyContaining<Program>()` + the query variant scan the assembly that contains `Program` and auto-register every `*Handler`. No `services.AddScoped<…>()` boilerplate per handler.
3. `mediator.HandleAsync(…)` is the **only** way you'd dispatch. Don't call handlers directly — every cross-cutting behavior (auth, audit, retry, logging) is wired through the mediator pipeline.

## 6. Run

```bash
dotnet run
# Account 5940e439-32ce-…: $100.00
```

## What's next

- **[DI Composition](di-composition.md)** — the full menu of `Add*Services()` extensions.
- **[CQRS Basics sample](../samples/01-cqrs-basics.md)** — the same app with the rejection-path (`InsufficientBalanceException`).
- **[Sample 2: Event Sourced](../samples/02-event-sourced.md)** — replace the in-memory repository with an event store.
