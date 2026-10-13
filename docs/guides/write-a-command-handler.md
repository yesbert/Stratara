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

### A handler that appends events needs command auditing

Every recorded event carries the identity of the command that caused it, and the command-audit
pipeline is what supplies it. A host whose handlers append events registers it:

```csharp
services.AddCommandAuditing();
```

It lives in `Stratara.EventSourcing.Pipeline.CommandAudit` and `AddCommandServices()` does not
register it for you, because where it sits in the pipeline is yours to decide — a host composing the
Orleans execution model keeps the aggregate-grain behaviour innermost, so the order matters.

Without it, `SaveChangesAsync` refuses the append before anything is written, with a message naming
this registration. A flow that appends without a command having preceded it — a timer, a sweep —
sets a causation id on the session it builds instead.

## Which tenant owns the events you append

A handler that writes events appends them through `IEventSource` and commits them with
`SaveChangesAsync`. Every event is recorded under the tenant that owns it — and that tenant is **not
simply the one in the caller's session**. The store takes, for each event, the first of these that
names a tenant:

1. the subject you stated for that event with `AppendOnBehalfOfAsync`, or for the stream's first event with `CreateOnBehalfOfAsync`;
2. the owner already resolved for the same stream earlier in the same batch;
3. the owner recorded on the stream's first event — its tenant, and its user where one was recorded;
4. the `TenantId` the event carries itself, when it implements `IAggregateCreationEvent`;
5. the tenant in the session, together with the session's data-owner user.

If none of them yields a tenant, the append fails with a message that names the event, the stream,
and the three ways to supply an owner: an explicit subject, a creation event, or a session tenant.

The stream comes before the session on purpose. A privileged operator whose session names another
tenant cannot silently re-home an existing aggregate by appending to it — the stream keeps the owner
it was created with, for every aggregate, whether or not the aggregate exposes a tenant property of
its own. The user is part of that owner: a stream whose first event was recorded for a user gives
every later event the same user, whichever user the session names, and a stream whose first event
names no user is given none. A creation event carries a tenant and no user, so a stream it creates
has no user. An aggregate whose events carried different owners could not be fully
erased, because each tenant's or user's erasure reaches only its own entries, and once one of those
keys was shredded it could not be rehydrated at all.

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

When the event does not carry the owner — an account an operator opens in a customer's tenant, a
platform-owned record created under any session — state it at creation instead:

<!-- stratara-snippet-ignore: narrative fragment - the event source, the aggregate and the ids come from the surrounding handler -->
```csharp
await events.CreateOnBehalfOfAsync<Account>(
    accountId,
    new AccountOpened(accountId, initialBalance),
    new EventSubject(customerTenantId),
    ct);
```

`CreateOnBehalfOfAsync` fails like `CreateAsync` when the stream exists, and like `AppendOnBehalfOfAsync`
when the subject names no tenant. The stated owner is the stream's owner from then on.

### When the session would decide a new stream's owner

The fifth candidate is the one that goes wrong quietly. A first event that states no owner and carries
none takes the session's tenant — right when a tenant's user creates a record in their own tenant, wrong
when an operator creates one for somebody else, and nothing fails either way. A host can decide:

```json
{
  "EventSourcing": {
    "NewStreamOwnerFromSession": "Warn"
  }
}
```

| Value | A new stream's first event that would take its owner from the session |
|---|---|
| `Allow` (default) | is recorded for the session's tenant, as before |
| `Warn` | is recorded for the session's tenant, and `102_007` (Warning) names the stream, the event type and the tenant — once per stream and batch, again when a failed save is retried |
| `Refuse` | fails before anything is staged, naming the stream, the event type and the ways to state an owner |

The setting touches nothing else: an owner stated with `CreateOnBehalfOfAsync` or `AppendOnBehalfOfAsync`,
a creation event that carries a tenant, and every append to a stream that already exists behave as
without it. Run `Warn` in a lower environment until the log stays quiet, then `Refuse`.

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

## Whose key a command's protected fields are under

A command can carry `[EncryptData]` fields like any other serialized type. The framework serializes
a command in two places: the copy that travels to a handler that runs later — the outbox, the bus,
the Orleans record — and the copy the command audit records. By default both are encrypted for the
**session the command was dispatched under**: the session's data-owner tenant, together with the
acting user in the travelling copy and with the session's data-owner user in the audit record.

That is right when the session's tenant is the tenant the data belongs to, and wrong in exactly the
flows the session has two identities for. A session with no tenant — an anonymous endpoint, or a
saga reacting to a fact one recorded, which runs under that fact's session — cannot encrypt a
`TenantScoped` field at all outside development. A session with *another* tenant encrypts it under
that tenant's key: nothing fails, and erasing the real owner leaves the audit record readable.

Only the command knows whose data it carries, so let it say so. `INamesDataOwner` is to a command
what `IAggregateCreationEvent` is to a stream's first event:

```csharp
public sealed record TrackPageVisit(
    Guid TenantId,
    [property: EncryptData(DataSensitivityLevel.TenantScoped)] string Address)
    : ICommand, INamesDataOwner
{
    Guid INamesDataOwner.DataOwnerTenantId => TenantId;
}
```

Implement it explicitly, as above, so nothing is added to the command's JSON. `DataOwnerUserId` is
optional and defaults to no user; implement it too when the data belongs to one user of the tenant.

What follows from the declaration:

- **Both copies are written for the named owner** — the same tenant and the same user, whatever
  session dispatched the command. The audit record names that owner as its data owner and the
  session's actor as its actor, so a tenant's view of the command log holds the commands that
  carried its data, whoever issued them.
- **The session is untouched.** The handler runs under the dispatching session; authorization,
  tenant isolation and correlation read it as before.
- **It authorizes nothing.** Nothing compares the named owner against the session. Whether the
  caller may act on that owner's data is decided as for any other command — by the stages you
  registered and by your handler. A command that also implements `ITenantScopedRequest` is still
  held to the session by the isolation guard; the two declarations are separate statements.
- **An empty owner is refused.** A command that implements the declaration and names
  `Guid.Empty` fails with an `ArgumentException` naming its type, where it is recorded and where it
  is dispatched, and nothing is recorded or sent. It does not fall back to the session.
- **The command can outlive its owner.** A command still waiting when its owner is erased is
  delivered, and its protected fields read as absent — see
  [Unreadable encrypted fields degrade rather than fail](encrypt-data-setup.md#unreadable-encrypted-fields-degrade-rather-than-fail).
  Give those fields a type that can be absent, and let the handler treat an absent one as "the owner
  is gone". The audit record stays as the evidence that the command was issued; its protected fields
  are unreadable from then on.

> **Rolling upgrade.** A host on a release before `INamesDataOwner` cannot read the owner a command
> carries. It does not run the handler with the fields emptied: under `Strict` bus-envelope integrity
> the signature does not verify, and otherwise the read fails authentication, so the message is
> retried and then kept. Upgrade every host that handles commands — command workers and silos —
> before any host dispatches a command that names an owner. The simplest order is two deployments:
> the framework release first, the declarations after. A command that names no owner is stored,
> published and signed exactly as before, so the release itself needs no ordering.

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

### A write between your read and your append

`AppendAsync` numbers the event after whatever the stream holds **when you append**, not after what
you read. A handler that rebuilds an aggregate, decides on it, and then appends therefore does not
conflict with a write that landed in between: its event is simply recorded after that write. Most of
the time that is harmless. It is not when the write in between changed what the decision rests on —
the entry was deleted, the order was cancelled — because the handler's fact is then recorded after a
fact that contradicts it. Re-reading just before the append narrows the window; it does not close it,
because the save that follows still takes time.

Two ways close it:

- **Name the version you decided on.** `AppendAtVersionAsync<TAggregate>(streamId, expectedVersion, @event)`
  and `AppendRangeAtVersionAsync<TAggregate>(streamId, expectedVersion, events)` number the events from
  `expectedVersion + 1`. If the stream has moved past it, `SaveChangesAsync` throws the same
  `ConcurrencyException` as above and records nothing. Read the version with `GetCurrentVersionAsync`
  *before* you rebuild the aggregate, so a write in between surfaces as a conflict rather than slipping
  through. A version the stream has not reached yet is refused at the append with
  `ArgumentOutOfRangeException`.
- **Let the host apply it to every read-then-write handler.** Set the option, and an append to a stream
  that the handler rebuilt through `IAggregationService.AggregateAsync` in the same scope is made on the
  condition of the version that rebuild saw — without touching a handler:

  ```json
  {
    "EventSourcing": {
      "AppendAgainstAggregatedVersion": true
    }
  }
  ```

  `AddEventSourcing()` reads the section. The condition is the version of the *first* rebuild of a stream
  in the scope — a second rebuild of the same stream, by a validator or a helper, does not move it. A
  rebuild bounded with `toVersion` sets no condition. A rebuild that found no stream sets the condition
  that the stream still does not exist, so a handler whose `AppendAsync` writes the first event conflicts
  with whoever created it first; `CreateAsync` refuses an existing stream — or one already staged in the
  batch — with `InvalidOperationException`. A successful save ends the condition on the streams it wrote and keeps it on those
  the handler read but has not written; a failed save ends it everywhere, so a handler that runs again
  reads again.

What the option costs: a handler that appends to a stream another writer touches concurrently now gets a
conflict where it used to succeed. Through `ICommandOutboxDispatcher` that is a redelivery and a fresh
read; through `IMediator` the caller sees the `ConcurrencyException`. The option is off by default.

## When a save committed but could not publish

`SaveChangesAsync` commits the events and then hands their bundle on to the outbox. On a host without
durable bundles, that handover can fail after the commit, when both the bus and the outbox's own table
are unavailable. The save then throws `CommittedEventsNotPublishedException`, which names the committed
streams, whatever ended the handover — a cancellation included. **The events are recorded; do not
append them again.** Readers that consume bundles did not receive them: a projection is repaired by a
replay, but a saga that reacts to bundles has missed them, and nothing repairs that — which is why a
host that cannot lose a bundle stores bundles with the commit. Such a host does not raise the
exception at all: its bundle is recorded in the same transaction as the events, and a handover that
fails afterwards loses nothing. Nothing in the framework runs the work again because of it: the
RabbitMQ and Azure Service Bus transports acknowledge the message and log an error (`108_113`); on the
Orleans execution model a recorded command is completed (`117_127`), a store reader counts the entry
as applied (`117_128`) and a durable timer counts as fired (`117_129`), each logged as an error; and
the retrying pipelines do not retry it.

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
