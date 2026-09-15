---
title: "Write a Command Handler"
description: "How to implement ICommandHandler and IQueryHandler, how Stratara discovers them, and which of the two a command with a result belongs to."
---

# Write a Command Handler

> **Derived page.** The behaviour described here is specified by the `mediator-dispatch` and
> `event-sourcing-store` capabilities under `openspec/specs/`. Those specifications are the source;
> this page explains and illustrates them. Where a specification and this page disagree, the
> specification is right and this page is a bug.

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

## Which tenant owns the events you append

A handler that writes events appends them through `IEventSource` and commits them with
`SaveChangesAsync`. Every event is recorded under the tenant that owns it — and that tenant is **not
simply the one in the caller's session**. The store takes, for each event, the first of these that
names a tenant:

1. the subject you stated for that event with `AppendOnBehalfOfAsync`;
2. the owner already resolved for the same stream earlier in the same batch;
3. the tenant recorded on the stream's first event;
4. the `TenantId` the event carries itself, when it implements `IAggregateCreationEvent`;
5. the tenant in the session.

If none of them yields a tenant, the append fails with a message that names the event, the stream,
and the three ways to supply an owner: an explicit subject, a creation event, or a session tenant.

The stream comes before the session on purpose. A privileged operator whose session names another
tenant cannot silently re-home an existing aggregate by appending to it — the stream keeps the owner
it was created with, for every aggregate, whether or not the aggregate exposes a tenant property of
its own. An aggregate whose events carried different owners could not be fully erased, because each
tenant's erasure reaches only its own entries, and once one of those keys was shredded it could not
be rehydrated at all.

So a new aggregate states its owner on its first event:

```csharp
public sealed record AccountOpenedInTenant(Guid AccountId, Guid TenantId, decimal InitialBalance)
    : IAggregateCreationEvent;

public sealed record OpenAccountInTenant(Guid AccountId, Guid TenantId, decimal InitialBalance) : ICommand;

public sealed class OpenAccountInTenantHandler(IEventSource events) : ICommandHandler<OpenAccountInTenant>
{
    public async Task HandleAsync(OpenAccountInTenant cmd, CancellationToken ct)
    {
        await events.CreateAsync<Account>(
            cmd.AccountId,
            new AccountOpenedInTenant(cmd.AccountId, cmd.TenantId, cmd.InitialBalance),
            ct);
        await events.SaveChangesAsync(ct);
    }
}
```

Every later append to that stream lands in the same tenant, whoever's session makes it.

### Appending on behalf of another subject

When an event genuinely belongs to someone other than the resolved owner, say so — never obtain it
by leaving something out. `AppendOnBehalfOfAsync` takes an `EventSubject` (a tenant id and an
optional user id) and overrides every other source, **for that one event only**:

```csharp
public sealed record CorrectBalance(Guid AccountId, Guid OwningTenantId, decimal Amount) : ICommand;

public sealed class CorrectBalanceHandler(IEventSource events) : ICommandHandler<CorrectBalance>
{
    public async Task HandleAsync(CorrectBalance cmd, CancellationToken ct)
    {
        await events.AppendOnBehalfOfAsync<Account>(
            cmd.AccountId,
            new AmountDeposited(cmd.AccountId, cmd.Amount),
            new EventSubject(cmd.OwningTenantId),
            ct);
        await events.SaveChangesAsync(ct);
    }
}
```

The actor recorded with the event is still the caller's session; only the owner changes. A subject
whose tenant id is empty fails the append at once — the message names the event and the stream,
nothing is recorded, and the store does **not** fall back to the stream, the event or the session,
because stating a subject already said that none of them applies.

## When two writers race on one stream

`SaveChangesAsync` writes the whole batch or none of it. If another writer has already written the
versions this batch is appending, the save throws `ConcurrencyException`
(`Stratara.Abstractions.EventSourcing`), whose `StreamId` and `AggregateTypeName` name the stream
and the aggregate type that lost the race. The staged batch is **discarded** — every stream in it —
so a retry starts from a fresh read of the aggregate, not from the events that just failed.

The exception is the same on every database provider the framework ships a store registration for:
a version collision the database refuses through its uniqueness constraint is a concurrency conflict
on PostgreSQL and on the SQLite store the test-support package registers alike. A save that fails
for any other reason propagates **unchanged** and is never presented as a conflict. Catch
`ConcurrencyException` and nothing broader: a conflict is worth retrying, anything else is not.

Who retries depends on how the command was dispatched:

- **Through `ICommandOutboxDispatcher`** — nothing to write. The command worker treats the conflict
  as a retry rather than a failure and redelivers the command, up to `MessageRetry:MaxConflictRequeues`
  times (default 100); the handler runs again and re-reads. See
  [When a handler cannot take a message](outbox-setup-rabbitmq.md#when-a-handler-cannot-take-a-message).
- **Through `IMediator`, in process** — the exception reaches the caller, who decides:

```csharp
app.MapPost("/accounts/{accountId:guid}/deposits", async (Guid accountId, decimal amount, IMediator mediator, CancellationToken ct) =>
{
    try
    {
        await mediator.HandleAsync(new DepositCommand(accountId, amount), ct);
        return Results.NoContent();
    }
    catch (ConcurrencyException conflict)
    {
        return Results.Conflict(new { conflict.StreamId, conflict.AggregateTypeName });
    }
});
```

Each conflict is also counted, on `event_source.append.conflicts`, tagged with `aggregate.type` and
`bucket.id` — the partition the stream fell in — so contention on one aggregate type or one hot
partition shows up before it shows up as latency.

## Mandatory hygiene

- **Max 7 constructor parameters** (this counts as one). If you need more, group them in a `sealed record` parameter object.
- **No magic numbers** — name your constants.
- **No manual retry loops** — pull a Polly pipeline from `Stratara.Resilience` via `ResiliencePipelineProvider<string>`.
- **No `Stopwatch`** — use `ActivitySource.StartActivity()` for timing.

## Logging

**Source-generated only** for new code. Per-package extension class under `Diagnostics/Extensions/Logger*Extensions.cs`:

<!-- stratara-snippet-ignore: shows the consumer's own LoggerMessage partial against its own LogEvents bucket -->
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

<!-- stratara-snippet-ignore: narrative fragment - the logger and the command come from the surrounding text -->
```csharp
logger.LogDepositApplied(cmd.Amount, cmd.AccountId);
```

Never call `logger.LogInformation(...)` directly. If you need expensive arguments (string-join, LINQ projection), wrap them in a small struct with `ToString()` — the source-gen formatter will defer evaluation until the channel is enabled. See `Stratara.Shared.Diagnostics.Extensions.DistinctEventTypeNames` for the canonical pattern.

## Tracing

Every dispatch is wrapped in a span named `Handle <RequestType>` — you never emit one yourself. The
spans come from the `Stratara.Application` activity source, the same one every other framework
trace uses, so a host that subscribes to that source sees each command and query as it passes
through the pipeline. Nothing has to be registered for this: `AddMediator()` and
`AddAuthorizingMediator<T>()` work on their own. A host that registers its own OpenTelemetry
`Tracer` keeps it, and the mediator emits through that instead.
