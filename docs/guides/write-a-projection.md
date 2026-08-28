# Write a Projection

> **Derived page.** The behaviour described here is specified by the `projections` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

A projection turns an event stream into a read model. `Stratara.Projections` discovers your
projections at startup, matches each incoming event bundle against the events a projection cares
about, and invokes only the matching methods.

`IProjection` (`Stratara.Projections.Abstractions`) is an **empty marker** — it declares nothing.
The contract is a naming convention: the runtime reflects over your class for `HandleAsync` methods
whose first parameter is an `IEvent<TEvent>`. That method's event type is what makes a projection
"interested" in an event; events outside that set are skipped without invoking the projection.

## The shape

```csharp
using JetBrains.Annotations;
using Stratara.Abstractions.EventSourcing;
using Stratara.Projections.Abstractions;

public sealed class AccountBalanceProjection(IAccountBalanceStore store) : IProjection
{
    [UsedImplicitly]
    private Task HandleAsync(IEvent<AccountOpened> @event, CancellationToken ct) =>
        store.UpsertAsync(@event.Data.AccountId, @event.Data.InitialBalance, ct);

    [UsedImplicitly]
    private Task HandleAsync(IEvent<AmountDeposited> @event, CancellationToken ct) =>
        store.AddAsync(@event.Data.AccountId, @event.Data.Amount, ct);

    [UsedImplicitly]
    private Task HandleAsync(IEvent<AmountWithdrawn> @event, CancellationToken ct) =>
        store.AddAsync(@event.Data.AccountId, -@event.Data.Amount, ct);
}
```

You write one `HandleAsync(IEvent<TEvent>, CancellationToken)` per event you care about — no manual
`switch`, no base-method override. The payload is `@event.Data`: `IEvent<TEvent>` re-declares `Data`
as the typed event, while the non-generic `IEvent` also carries `StreamId`, `Version`, `TenantId`
and `UserId`.

Handlers may be **private** — discovery uses `BindingFlags.NonPublic`, so they stay off the
projection's public surface. Mark them `[UsedImplicitly]` so analyzers don't flag them; the runtime
is the only caller. Where you store the read model is your choice — a Postgres table via your own
DbContext, an in-memory dictionary, an Elasticsearch document. `Stratara.Projections`' own
`TenantProjection` is written exactly this way and is the canonical example.

## Register it

```csharp
builder.Services
    .AddProjectionWorker(builder.Configuration)                       // runtime + hosted service
    .AddProjectionsFromAssemblyContaining<AccountBalanceProjection>(); // your IProjection types
```

Both calls matter. `AddProjectionsFromAssemblyContaining<T>()` registers the projections **and** the
event types they consume (so payloads deserialize); `AddProjectionWorker(IConfiguration)` registers
the manager, the method invoker, and the hosted service that consumes the event-bundle subscription.
Most hosts take both from the `AddEventProjectionWorkerServices()` composite in
`Stratara.EventSourcing.WorkerDefaults`.

### Event-only hosts (no handler dependencies)

If a host must deserialize events off the bus but should *not* wire the projection classes —
a worker whose projections depend on runtime services it deliberately doesn't compose — register
only the event types:

```csharp
services.AddDomainEventTypesFromAssemblyContaining<AccountOpened>();
```

This adds only the aggregates' `Apply(SomeEvent)` parameter types to the trusted-type allowlist — no
aggregate types, no handler classes. See the [DI Extensions Cheatsheet](../reference/di-extensions-cheatsheet.md).

## Idempotency is your job

Event bundles are delivered at-least-once, so a projection may see the same event twice — after a
retry or during a replay. Write handlers that converge rather than accumulate:

| Pattern | Safe on redelivery? |
|---|---|
| `store.UpsertAsync(id, absoluteValue)` | Yes — replays write the same value |
| `store.AddAsync(id, delta)` | **No** — a redelivery double-counts unless you guard on `@event.Version` |

The `AddAsync` lines above are the honest trade-off this example makes for brevity. In production,
either derive the absolute value, or record the last applied `Version` per stream and skip anything
already seen.

### The two races, and the one that needs help

Two things happen routinely and are not faults. A row can vanish between your read and your write,
because a cascading delete got there first. And a delete can conflict with a concurrent delete of
the same row — the end state you wanted has simply been reached by someone else.

The first needs no helper. Load the row and return when it is not there:

```csharp
var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
if (tenant is null) { return; }
```

The second is the one that is easy to get wrong, so the framework ships it:

```csharp
await repository.DeleteAsync(@event.StreamId, cancellationToken);

await transaction.SaveChangesIdempotentAsync(
    ct => repository.ExistsAsync(@event.StreamId, ct), cancellationToken);
```

**The probe is the point.** On a conflict the helper asks whether the target is still there. Gone
means a concurrent bundle reached the same end state, and the commit is treated as satisfied. Still
there means a second writer changed a live row — a real conflict, rethrown, and the bundle fails as
it must. Catching `ConcurrencyConflictException` broadly instead would turn "a failing projection
stops the bundle" into a guarantee that holds only where nobody used the helper.

## What the framework does not do

There is **no checkpoint store**. Projections are driven push-wise off the event bus; Stratara does
not track how far each projection has progressed, so there is no consumer-lag metric and no
resume-from-sequence. The observability you get is throughput and latency
(`projection.events.processed`, `projection.bundle.duration`). If you need lag, you own the
checkpoint. Replay of the historical stream is coordinated separately, via the
`IProjectionReplayState` in `Stratara.Outbox.RabbitMQ`.

## Replay is destructive, and it is all-or-nothing

A replay is not a repair tool you reach for casually. Three properties, in the order they will
surprise you:

**It empties before it rebuilds.** A replay marks itself active, truncates *every registered read
model*, then replays the whole stream from the beginning in batches. The truncation is what makes it
a rebuild rather than a second application of events on top of state that already reflects them —
but it means the read side is empty from the instant the replay starts, and stays that way until the
rebuild passes each row again.

**There is no per-projection scope.** You cannot replay one projection. Every read model registered
in the host is emptied, including the ones that were fine.

**It runs on request, with no confirmation step.** The worker does not start one at host start-up —
it subscribes and waits. But when a request arrives it begins immediately. There is no dry run, no
"are you sure", and no built-in guard on who may ask.

And when it ends, it marks itself inactive **whether it succeeded or not**. A replay that dies
half-way leaves you with partially rebuilt read models and no flag saying so. Treat a failed replay
as "run it again", not as "it stopped safely".

**A host that is killed does not get to mark anything.** Failing is an ending; being killed is not.
A `SIGKILL`, a container stop, an out-of-memory kill or a reboot leaves the replay with no chance to
clear its own marking — and while the marking stands, publication stays suppressed for the whole
host: commands are recorded instead of sent, the caller gets an identifier and a success response for
a command that will never run, and the outbox does not drain.

So the marking is held on a lease that the replay renews each time it reports progress. Nobody
renewing it means nobody is replaying, and it lapses on its own. Set the lease longer than your
slowest stretch between two progress reports — the slowest batch, and the read-model truncation that
precedes the first report:

```csharp
builder.Services.Configure<ProjectionReplayOptions>(
    o => o.LeaseSeconds = 600);   // default 300
```

Err long. Too long only delays the clearing of a marking whose replay already died; too short lets
the marking lapse while the replay is still running, which resumes suppressed publication against
half-rebuilt read models and tells nobody.

One thing a version bump does not do for you: a marking that is *already* stuck from before you
adopted the lease was written without an expiry and does not gain one. Clear it once — an explicit
deactivation, or let the next replay's own completion clear it.

## See also

- **[Sample 2 — Event Sourced](../samples/02-event-sourced.md)** — an aggregate and its projection end to end.
- **[Write a Saga](write-a-saga.md)** — the sibling pattern that reacts to events by issuing commands.
