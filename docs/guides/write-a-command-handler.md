# Write a Command Handler

A command handler implements `ICommandHandler<TCommand>` (no result) or `IQueryHandler<TCommand, TResult>` (the unified interface that also handles `ICommand<T>`). Stratara discovers them via `AddCommandHandlersFromAssemblyContaining<T>()`.

## Choose the command shape

| Scenario | Marker | Routing |
|---|---|---|
| Mutation without result, fire-and-forget | `ICommand` | `ICommandOutboxDispatcher` (async via outbox) |
| Mutation with synchronous result (e.g. setup, "create-and-return-id" flows) | `ICommand<TResult>` | `IMediator` (in-process) |
| Mutation that destroys infrastructure (drop outbox, recreate write-store) | `ICommand<TResult>` | `IMediator` (must stay in-process) |
| Mutation that signals process-local state | `ICommand` | `IMediator` (in-process) |
| Read | `IQuery<TResult>` | `IMediator` |

See **[Routing Conventions](../reference/routing-conventions.md)** for the full table.

## Define the command + handler

```csharp
using Stratara.Abstractions.Mediator;

public sealed record DepositCommand(Guid AccountId, decimal Amount) : ICommand;

public sealed class DepositHandler(IAccountRepository repo) : ICommandHandler<DepositCommand>
{
    public async Task HandleAsync(DepositCommand cmd, CancellationToken ct)
    {
        var account = await repo.GetAsync(cmd.AccountId, ct);
        account.Deposit(cmd.Amount);
        await repo.SaveAsync(account, ct);
    }
}
```

## Register

```csharp
services.AddCommandHandlersFromAssemblyContaining<DepositHandler>();
```

That's it — the handler is now resolved per-scope and dispatched whenever `mediator.HandleAsync(new DepositCommand(...))` is called.

## Mandatory hygiene

- **Max 7 constructor parameters** (this counts as one). If you need more, group them in a `sealed record` parameter object.
- **No magic numbers** — name your constants.
- **No manual retry loops** — pull a Polly pipeline from `Stratara.Resilience` via `IResiliencePipelineProvider<string>`.
- **No `Stopwatch`** — use `ActivitySource.StartActivity()` for timing.

## Logging

**Source-generated only** for new code. Per-package extension class under `Diagnostics/Extensions/Logger*Extensions.cs`:

```csharp
public static partial class LoggerAccountExtensions
{
    [LoggerMessage(
        EventId = LogEvents.MyApp.DepositApplied,
        Level = LogLevel.Information,
        Message = "Deposited {Amount} into account {AccountId}.")]
    public static partial void LogDepositApplied(this ILogger logger, decimal amount, Guid accountId);
}
```

Then in the handler:

```csharp
logger.LogDepositApplied(cmd.Amount, cmd.AccountId);
```

Never call `logger.LogInformation(...)` directly. If you need expensive arguments (string-join, LINQ projection), wrap them in a small struct with `ToString()` — the source-gen formatter will defer evaluation until the channel is enabled. See `Stratara.Shared.Diagnostics.Extensions.DistinctEventTypeNames` for the canonical pattern.
