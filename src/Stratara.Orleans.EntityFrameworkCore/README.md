# Stratara.Orleans.EntityFrameworkCore

> **Derived.** The behaviour described here is specified under `openspec/specs/`, in the
> `orleans-execution` and `event-sourcing-store` capabilities. Those specifications are the source;
> this page explains them.

> **License:** [MIT](../../LICENSE).

Entity Framework Core persistence for the Stratara Orleans execution model: the readers that return
the event store in commit order and the checkpoint store the store-reading projections and sagas
resume from. The columns and tables they need are declared by the framework's write and read
contexts in `Stratara.EventSourcing.EntityFrameworkCore`, so a context derived from those carries
them without further configuration.

## What's in the box

| Part | What it does |
|---|---|
| `PostgresTransactionIdReader<TContext>` | Reads in commit order on PostgreSQL, adding no work to an append |
| `PortableCounterReader<TContext>` | Reads in commit order on any relational provider, through a per-partition counter |
| `PartitionCounterInterceptor` | Maintains the per-partition counter inside the appending transaction |
| `AddStrataraProjectionCheckpoints<TReadContext>()` | Keeps the checkpoints in the read context |

## Quick start

```csharp
builder.Services
    .AddSingleton<ICommittedPositionReader, PostgresTransactionIdReader<AppWriteDbContext>>()
    .AddStrataraProjectionCheckpoints<AppReadDbContext>()
    .AddStrataraProjectionGrains();
```
