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
| `PortableCounterReader<TContext>`, `AddStrataraPortableCounterReader<TWriteContext>()` | Reads in commit order on any relational provider, through a per-partition counter; verified on PostgreSQL and on SQLite |
| `PartitionCounterInterceptor` | Maintains the per-partition counter inside the appending transaction — in every process that appends |
| `PartitionCounterBackfill` | Positions unpositioned entries after the partition's counter, never moving a position already handed out, so checkpoints stay true |
| `CommitTransactionIdBackfill` | Stamps the entries a PostgreSQL store held before the commit record existed, in append order and bounded batches, once |
| `AddStrataraProjectionCheckpoints<TReadContext>()` | Keeps the checkpoints in the read context |
| `AddStrataraIntentStore<TWriteContext>()` | Records the commands the execution model's dispatcher accepts, in the outbox table, and claims a due batch in two statements |
| `AddStrataraExecutionModelReset<TReadContext>()` | Clears reminders, membership, directory and the host's checkpoints while no silo runs |
| `IStoreReaderSeeding` (registered with the store-reading roles) | Seeds the host's checkpoints at the store's head before a first start on a populated store |

## Quick start

The read side — projections that read the store in commit order from a checkpoint:

```csharp
builder.Services
    .AddSingleton<ICommittedPositionReader, PostgresTransactionIdReader<AppWriteDbContext>>()
    .AddStrataraProjectionCheckpoints<AppReadDbContext>()
    .AddStrataraProjectionGrains();
```

The write side — commands recorded before the dispatch returns and resumed after a crash:

```csharp
builder.Services
    .AddStrataraOrleansCommandDispatcher()
    .AddStrataraIntentStore<AppWriteDbContext>()
    .AddStrataraSingletonWork<OutboxDrainWork>(OutboxDrainWork.WorkName);
```

The record is committed in a transaction of its own, not with what the caller writes.
