# Stratara.Orleans

> **Derived.** The behaviour described here is specified under `openspec/specs/`, in the
> `orleans-execution` capability. Those specifications are the source; this page explains them.

> **License:** [MIT](../../LICENSE).

The Orleans execution model for the Stratara event-sourced stack. Commands, projections, sagas,
timers and singleton work run as virtual actors on a cluster, so that a committed fact is never lost
to a crash, one aggregate has one writer across the whole deployment, and work that must happen once
per cluster needs no lock.

The model is adopted one role at a time, beside the bus workers or instead of them. Existing
handlers, projections and sagas run unchanged.

## What's in the box

| Role | Registration |
|---|---|
| Commands in their aggregate's activation | `AddStrataraAggregateGrains()` |
| Commands recorded before the call returns and resumed after a crash | `AddStrataraOrleansCommandDispatcher()` |
| Projections that read the store in commit order from a checkpoint | `AddStrataraProjectionGrains()` |
| Sagas that read the store in commit order from a checkpoint | `AddStrataraSagaGrains()` |
| Work that runs once per cluster | `AddStrataraSingletonWork<TWork>()` |
| Owner-checked durable timers | `AddStrataraDurableTimers()` |

The commit-order readers, the checkpoint store and the store-schema additions live in
`Stratara.Orleans.EntityFrameworkCore`.

## Quick start

```csharp
builder.UseOrleans(silo => { /* clustering, reminders and the durable grain directory */ });
builder.AddEventProjectionServices();
builder.Services
    .AddProjectionsFromAssemblyContaining<IAppMarker>()
    .AddSingleton<ICommittedPositionReader, PostgresTransactionIdReader<AppWriteDbContext>>()
    .AddStrataraProjectionCheckpoints<AppReadDbContext>()
    .AddStrataraProjectionGrains();
```
