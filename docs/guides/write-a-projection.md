# Write a Projection

Projections are read-models driven by event bundles from the bus. The `EventProjectionWorker` (registered via `AddEventProjectionWorkerServices()`) consumes the event-bundle topic and routes each event to every projection that's marked it as "relevant".

## The interface

```csharp
public interface IProjection
{
    Task HandleAsync(IReadOnlyList<IEvent> relevantEvents, CancellationToken cancellationToken);
}
```

Stratara doesn't dictate *how* you store the read-model — that's your decision (an in-memory dictionary, a Postgres table via your own DbContext, an Elasticsearch document, …).

## Declare which events you care about

Stratara discovers a projection's "interesting" events by reflection: it scans the projection class for `HandleAsync(SomeEvent)` overloads.

```csharp
public sealed class AccountBalanceProjection(IAccountBalanceStore store) : IProjection
{
    public async Task HandleAsync(IReadOnlyList<IEvent> relevantEvents, CancellationToken ct)
    {
        foreach (var ev in relevantEvents)
        {
            switch (ev)
            {
                case IEvent<AccountOpened> opened:
                    await HandleAsync(opened.Payload, ct);
                    break;
                case IEvent<AmountDeposited> deposited:
                    await HandleAsync(deposited.Payload, ct);
                    break;
                case IEvent<AmountWithdrawn> withdrawn:
                    await HandleAsync(withdrawn.Payload, ct);
                    break;
            }
        }
    }

    private Task HandleAsync(AccountOpened ev, CancellationToken ct) =>
        store.UpsertAsync(ev.AccountId, ev.InitialBalance, ct);

    private Task HandleAsync(AmountDeposited ev, CancellationToken ct) =>
        store.AddAsync(ev.AccountId, ev.Amount, ct);

    private Task HandleAsync(AmountWithdrawn ev, CancellationToken ct) =>
        store.AddAsync(ev.AccountId, -ev.Amount, ct);
}
```

The per-event `HandleAsync(SomeEvent, …)` overloads are what `AddProjectionsFromAssemblyContaining<T>()` scans for. Stratara reads them by reflection to compute the projection's "event allowlist" — events outside the allowlist are skipped without invoking the projection.

## Register

```csharp
services.AddProjectionsFromAssemblyContaining<AccountBalanceProjection>();
```

You also need a worker host that runs the projection worker. Typically:

```csharp
builder.AddEventProjectionWorkerServices();
```

## Idempotency

Sagas + projections must be **idempotent**: the bus can replay event bundles after a transient broker outage. Two common patterns:

1. **Replay-safe upsert** — `INSERT … ON CONFLICT DO UPDATE` patterns; same event lands at the same result.
2. **Event-id deduplication** — the read-store tracks already-projected event IDs and skips duplicates.

## Backfill / replay

`ProjectionReplayWorker` walks the historical event stream and re-applies events to a projection from scratch. Useful when you add a new projection — boot the replay-worker once against the new projection, then switch to the live `EventProjectionWorker`.
