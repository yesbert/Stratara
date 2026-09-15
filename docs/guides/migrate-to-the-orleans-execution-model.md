---
title: "Migrate to the Orleans Execution Model"
description: "What a host that runs the bus workers adds to run on the Orleans execution model: the silo, the schema additions, the commit-order reader, and one registration per role."
---

# Migrate to the Orleans Execution Model

> **Derived page.** The behaviour described here is specified by the `orleans-execution`,
> `host-composition` and `event-sourcing-store` capabilities under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them. Where the two disagree, the
> specification is right and this page is a bug.

For a host that runs Stratara with the bus workers and moves to the
[Orleans execution model](../concepts/orleans-execution-model.md), block by block. Handlers,
projections and sagas do not change. What changes is registration.

## Add the packages

```bash
dotnet add package Stratara.Orleans
dotnet add package Stratara.Orleans.EntityFrameworkCore
```

The silo's clustering, reminder and directory providers are the host's choice; the examples below use
the ADO.NET providers on PostgreSQL and the Redis grain directory.

## Register the silo

```csharp
var orleansDb = builder.Configuration.GetConnectionString("orleans")!;
var redis = StackExchange.Redis.ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("redis")!);

builder.UseOrleans(silo => silo
    .UseAdoNetClustering(options => { options.Invariant = "Npgsql"; options.ConnectionString = orleansDb; })
    .UseAdoNetReminderService(options => { options.Invariant = "Npgsql"; options.ConnectionString = orleansDb; })
    .AddStrataraOrleans((s, name) => s.AddRedisGrainDirectory(name, options => options.ConfigurationOptions = redis)));
```

`AddStrataraOrleans` registers the storage-backed grain directory the model's single-activation grains
use, under the name it passes to your callback. A silo that runs the model's grains without it fails at
start with a message naming this call. The ADO.NET providers ship no database scripts: apply Orleans'
main, clustering and reminders scripts for your database once, in that order, from the Orleans release
you run.

## Migrate the schema

The additions are declared by the framework's write and read contexts in
`Stratara.EventSourcing.EntityFrameworkCore`, so a context derived from them carries them. Generate a
migration after upgrading; a store that never runs the execution model carries the columns and never
fills them.

| Context | Addition | What it is for |
|---|---|---|
| Write | `event_stream_entry.partition_position` (nullable, indexed) | An entry's position in its partition, for the portable reader |
| Write | table `partition_position`, one row per partition | The counter the positions are handed out from |
| Write, PostgreSQL only | `event_stream_entry.commit_transaction_id` (`xid8`, filled by the database, indexed) | The transaction that inserted the entry, for the native reader |
| Write | `outbox_entry.aggregate_id`, `heavy` | Where a recorded command runs |
| Write | `outbox_entry.attempt_count`, `last_handed_over_at`, `kept_at`, `last_failure` (up to 2048 characters) | The bounded resume of a recorded command |
| Read | table `projection_checkpoint` (`projection`, `partition`, `reader`, `position`) | Where each store reader resumes |

## Choose a commit-order reader

Projections and sagas read the store through one `ICommittedPositionReader`.

**On PostgreSQL**, the native reader adds no work to an append:

```csharp
builder.Services.AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<AppWriteDbContext>>();
```

**On any other relational store**, the portable reader orders by the partition counter:

```csharp
builder.Services.AddStrataraPortableCounterReader<AppWriteDbContext>();
```

The write context must add `PartitionCounterInterceptor` to its interceptors so every append is
positioned. A store that already holds entries is positioned once, after migrating and before the first
start, with `PartitionCounterBackfill.RunAsync`; the host refuses to start while an entry without a
position remains. Running the backfill again changes nothing. A checkpoint the portable reader wrote
before a backfill is no longer meaningful and must be reset.

## Adopt the roles

Each role is one call after the composite the host already has.

| Role today | Add after the composite | What changes |
|---|---|---|
| API or backend host (`AddBackendServices`) | `AddStrataraOrleansCommandDispatcher()` and `AddStrataraIntentStore<AppWriteDbContext>()` | `ICommandOutboxDispatcher` records the command in the outbox table and hands it to its activation instead of publishing it. Composes with `AddAuthorizingCommandOutboxDispatcher()` in either order |
| Command worker (`AddCommandWorkerServices`) | `AddStrataraAggregateGrains()` | A command that names an aggregate runs in that aggregate's grain. Register it after every other pipeline behaviour |
| Outbox worker (`AddOutboxWorkerServices`) | `AddStrataraSingletonWork<OutboxDrainWork>()` on the silos, and retire the worker host | The drain runs once per cluster and resumes the commands a crash left behind; the Redis outbox lock is no longer needed |
| Projection worker (`AddEventProjectionWorkerServices`) | `builder.AddEventProjectionServices()` instead, then `AddStrataraProjectionCheckpoints<AppReadDbContext>()` and `AddStrataraProjectionGrains()` | One grain per projection and partition reads the store from a checkpoint; the bus-fed worker is not registered |
| Saga worker (`AddSagaWorkerServices`) | `builder.AddSagaServices()` instead, then `AddStrataraSagaGrains()` | One grain per partition hands each fact to the sagas; stateful processes derive from `SagaProcess<TState>` |
| Heavy command worker (`AddHeavyCommandWorkerServices`) | `ConfigureStrataraHeavyWork(o => o.ClusterWideLimit = …)` | Heavy commands run in a bounded pool per silo under cluster-wide permits; the heavy lane and its host go |
| Timeouts the host built itself | `AddStrataraDurableTimers()` with one `ITimerOwners` and one `ITimerHandler` | Owner-checked, durable, once per cluster; the host's ports may be registered before or after the model |

During a rollout a host can run both models at once — the bus consumer and the grains both apply
idempotently. `AddStrataraProjectionGrains` and `AddStrataraSagaGrains` take `hybrid: true` to keep
publishing bundles to the bus while the grains read the store.

## A full replay on a host whose projections read the store

A full replay returns every store reader's checkpoint to the beginning before the read models are
emptied, and the readers read from the beginning once the replay ends. Register the host's
`IProjectionViewTruncator` **before** `AddStrataraProjectionGrains`; a truncator registered after it fails
the host at start. To rebuild a single projection, implement `IRebuildableProjection` and call
`IProjectionRebuilder.RebuildAsync` — only that projection's model is emptied and re-read.

## Settings

Every setting is validated when the host starts; an invalid one fails the start and names itself.

| Settings | Defaults |
|---|---|
| `OrleansDispatchOptions` | `IntentGrace` 30 s, `CompletionWindow` 20 ms, `CompletionBatchSize` 64. The resume bound is `MessageRetryOptions.MaxDeliveryAttempts` |
| `HeavyWorkOptions` | `ClusterWideLimit` 8, `PermitRetry` 100 ms, `PermitLease` 30 s |
| `ProjectionGrainOptions`, `SagaGrainOptions` | `BatchSize` 500, `PollInterval` 5 s, `KeepAlivePeriod` 1 min |
| `CommitOrderOptions` | `PartitionCount` 16, `MaintainPartitionCounter` true |
| `SingletonWorkOptions` | `KeepAlivePeriod` 1 min |
| `OutboxDrainOptions` | `PollingInterval` 5 s, `BatchSize` 100 |
| `DurableTimerOptions` | `RetryPeriod` 1 min, `DueTolerance` 500 ms |

A keep-alive or retry period is kept as a reminder and must be at least the runtime's minimum reminder
period.

## What gets stronger

| Guarantee | With the bus workers | With the execution model |
|---|---|---|
| One writer per aggregate | Per worker process; two processes can conflict and requeue | One activation per aggregate across the cluster |
| A committed fact reaches every projection and saga | Only if the publish after the commit succeeded, unless durable bundles are on | Always, from the store, in commit order |
| A projection or saga fails on an entry | The bundle is dead-lettered and the stream moves on | The partition stops at the entry and retries it |
| Rebuilding a read model | A full replay of every read model | One projection at a time, while the others keep applying |
| Singleton work | A distributed lock or a one-instance deployment | Once per cluster, without a lock |
| Timeouts | Left to the host | Durable, owner-checked, once per cluster |
| Order of two commands to one aggregate from one scope | Not promised | Kept |

## Handler habits that become unnecessary but stay harmless

- **Retrying on `ConcurrencyException`.** Two commands on one aggregate no longer run at the same time.
- **Tolerating a second delivery.** Still required: a handler that completed before a crash recorded its
  completion runs again.
- **Throwing `PrecedingFactMissingException`.** Still meaningful across partitions; the entry is retried
  without advancing the checkpoint.

See also [Operate the Orleans Execution Model](operate-the-orleans-execution-model.md).
