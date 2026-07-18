# Write a Projection

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

## What the framework does not do

There is **no checkpoint store**. Projections are driven push-wise off the event bus; Stratara does
not track how far each projection has progressed, so there is no consumer-lag metric and no
resume-from-sequence. The observability you get is throughput and latency
(`projection.events.processed`, `projection.bundle.duration`). If you need lag, you own the
checkpoint. Replay of the historical stream is coordinated separately, via the
`IProjectionReplayState` in `Stratara.Outbox.RabbitMQ`.

## See also

- **[Sample 2 — Event Sourced](../samples/02-event-sourced.md)** — an aggregate and its projection end to end.
- **[Write a Saga](write-a-saga.md)** — the sibling pattern that reacts to events by issuing commands.
