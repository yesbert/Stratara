# Stratara.Orleans.EntityFrameworkCore

> **Derived.** The behaviour described here is specified under `openspec/specs/`, in the
> `orleans-execution` and `event-sourcing-store` capabilities. Those specifications are the source;
> this page explains them.

> **License:** [MIT](../../LICENSE).

Entity Framework Core persistence for the Stratara Orleans execution model: the readers that return
the event store in commit order, the checkpoint store the store-reading projections and sagas resume
from, and the additions to the store schema both need.

## What's in the box

| Part | What it does |
|---|---|
| `PostgresTransactionIdReader<TContext>` | Reads in commit order on PostgreSQL, adding no work to an append |
| `PortableCounterReader<TContext>` | Reads in commit order on any relational provider, through a per-partition counter |
| `PartitionCounterInterceptor` | Maintains the per-partition counter inside the appending transaction |
| `CommitOrderModel` | Adds the commit-order columns and the counter table to the write model |
| `ProjectionCheckpointModel` | Adds the checkpoint table to the read model |
| `AddStrataraProjectionCheckpoints<TReadContext>()` | Keeps the checkpoints in the read context |

## Quick start

```csharp
public sealed class AppWriteDbContext(DbContextOptions<AppWriteDbContext> options)
    : WriteDbContext<AppWriteDbContext>(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        CommitOrderModel.Apply(modelBuilder, postgres: Database.IsNpgsql());
    }
}
```
