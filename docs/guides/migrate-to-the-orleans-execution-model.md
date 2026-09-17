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
dotnet add package Microsoft.Orleans.Clustering.AdoNet
dotnet add package Microsoft.Orleans.Reminders.AdoNet
dotnet add package Microsoft.Orleans.GrainDirectory.Redis
```

The silo's clustering, reminder and directory providers are the host's choice; the examples below use
the ADO.NET providers on PostgreSQL and the Redis grain directory, which are the three provider packages
above. `Stratara.Orleans` brings the Orleans runtime and reminders and no provider.

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
use, under the name it passes to your callback, and the placement filters every silo of the model needs.
A silo that runs the model's grains without it fails at start with a message naming this call. The
ADO.NET providers create no tables: apply Orleans' main, clustering and reminders scripts for your database
once, in that order, from the Orleans release you run — for PostgreSQL `PostgreSQL-Main.sql`,
`PostgreSQL-Clustering.sql` and `PostgreSQL-Reminders.sql`, shipped inside the
`Microsoft.Orleans.Clustering.AdoNet` and `Microsoft.Orleans.Reminders.AdoNet` packages (in the package's
folder under your NuGet cache, or in the Orleans repository under `src/AdoNet`). The `orleans` connection
string may point at the write-store database or at one of its own; the reset needs the same string.

A host that only dispatches commands — an API host — may join the cluster as an Orleans client with
`UseOrleansClient` instead of running a silo: the dispatcher needs only the grain factory the client
provides, and the aggregates run on the silos of the command role.

## Migrate the schema

The additions are declared by the framework's write and read contexts in
`Stratara.EventSourcing.EntityFrameworkCore`, so a context derived from them carries them. Generate a
migration after upgrading — every host on that package does, whether or not it adopts the execution model,
because appends and outbox writes fail against a table without the columns. A store that never runs the
execution model carries the columns and never fills them.

| Context | Addition | What it is for |
|---|---|---|
| Write | `event_stream_entry.partition_position` (nullable, indexed) | An entry's position in its partition, for the portable reader |
| Write | table `partition_position`, one row per partition | The counter the positions are handed out from |
| Write, PostgreSQL only | `event_stream_entry.commit_transaction_id` (`xid8`, filled by the database, indexed) | The transaction that inserted the entry, for the native reader |
| Write | `outbox_entry.aggregate_id`, `heavy` | Where a recorded command runs |
| Write | `outbox_entry.attempt_count`, `last_handed_over_at`, `kept_at`, `last_failure` (up to 2048 characters) | The bounded resume of a recorded command |
| Read | table `projection_checkpoint` (`projection`, `partition`, `reader`, `position`) | Where each store reader resumes. Keyed by consumer — the projection class's simple name, `sagas` for every saga — not by deployment, so renaming a projection class starts it at the beginning; see [Sharing a read store](operate-the-orleans-execution-model.md#sharing-a-read-store) |

### A populated event table on PostgreSQL

The migration EF Core generates for `commit_transaction_id` adds the column non-null with its volatile
default, and PostgreSQL rewrites the whole table for that under an exclusive lock, stamping every existing
row with the migration's own transaction id. The native reader never ends a batch inside one transaction,
so it would return each partition's entire history as one batch. On a populated table, edit the generated
migration into three steps and run the backfill between them, **while nothing appends**:

```sql
ALTER TABLE event_stream_entry ADD COLUMN commit_transaction_id xid8 NULL;
```

```csharp
await using var backfillScope = app.Services.CreateAsyncScope();
await using var writeContext = await backfillScope.ServiceProvider.GetRequiredService<IDbContextFactory<AppWriteDbContext>>().CreateDbContextAsync();
var stamped = await CommitTransactionIdBackfill.RunAsync(writeContext, batchSize: 1_000);
```

```sql
ALTER TABLE event_stream_entry ALTER COLUMN commit_transaction_id SET DEFAULT pg_current_xact_id();
ALTER TABLE event_stream_entry ALTER COLUMN commit_transaction_id SET NOT NULL;
CREATE INDEX ix_event_stream_entry_commit_transaction_id ON event_stream_entry (commit_transaction_id);
```

Adding the column nullable without a default is a metadata change; the backfill stamps the history in the
order it was appended, in batches of the given size, each under a transaction of its own, so the reader
returns history in batch-sized groups in append order; setting the default and the constraint afterwards
touches no row. An entry appended during the backfill receives no id before the default is set and a
later id than newer entries after it, which inverts the order inside its stream — stop the appending
hosts for the window. Running the backfill again on a stamped table changes nothing. An empty table takes
the generated migration as it is.

## Upgrade in this order

1. **Migrate the schema before the first 4.1 host starts.** Every addition is nullable or defaulted, so
   the 4.0 hosts keep running against the migrated schema; a 4.1 host against the old schema fails its
   first append. On a populated PostgreSQL table, migrate as the previous section says.
2. **Stop the bus outbox worker before the first silo with an intent store runs the drain**
   (see [Adopt the roles](#adopt-the-roles)).
3. **Seed the checkpoints** of the projections and sagas the silos will register, from the silo's own
   composition, while no silo runs — see [Start on a populated store](#start-on-a-populated-store).
4. **Start the silos**, at least two, and watch [what to watch](operate-the-orleans-execution-model.md#what-to-watch).
5. **Switch the API host** to the execution model's dispatcher; let the command worker's queue drain
   before stopping the command worker hosts.
6. **Stop the bus projection and saga workers**, keeping `hybrid: true` only while a consumer outside this
   deployment still needs the bundles on the bus, then retire the queues nothing consumes any more in the
   order [After the cut-over: the bus queues](#after-the-cut-over-the-bus-queues) gives.

## Start on a populated store

A store reader without a checkpoint starts at the beginning of the store. A host whose read models are
already current seeds a checkpoint at the store's head for every projection and saga it registers before
its first start, so that its first start applies only what commits afterwards:

```csharp
await using var seedingScope = app.Services.CreateAsyncScope();
var seeded = await seedingScope.ServiceProvider.GetRequiredService<IStoreReaderSeeding>().SeedAtHeadAsync();
```

Run it once, while no silo of the cluster runs, from the composition that calls `AddStrataraProjectionGrains`
or `AddStrataraSagaGrains` — the consumers it seeds are the ones registered there — after the schema is
migrated and before the host is started. A consumer that already has a checkpoint is left as it is; a
projection registered later starts at the beginning, which is what a new projection needs; and a read model
that is to be rebuilt from the beginning is not seeded, or is reset first. The report says how many
checkpoints were seeded and how many already existed.

## Choose a commit-order reader

Projections and sagas read the store through one `ICommittedPositionReader`.

**On PostgreSQL**, the native reader adds no work to an append:

```csharp
builder.Services.AddSingleton<ICommittedPositionReader, PostgresTransactionIdReader<AppWriteDbContext>>();
```

**On any other relational store**, the portable reader orders by the partition counter:

```csharp
builder.Services.AddStrataraPortableCounterReader<AppWriteDbContext>();
```

The write context must add `PartitionCounterInterceptor` to its interceptors so every append is
positioned — in **every process that appends to the store**, not only in the hosts that read. The framework
does not add it. `CommitOrderOptions.MaintainPartitionCounter` switches nothing: it is obsolete, warns where it is
set, and is removed with the next major version; the interceptor is what maintains the counter. Within one save,
positions follow each stream's version order. A read stops at an entry appended without a position rather than skipping it: the
partition stops advancing, the failure names the entry, and positioning it with the backfill lets the
partition continue. The partition count is fixed once the store holds positions — lowering it would merge
partitions whose positions overlap, the framework offers no renumbering, and a host refuses to start with a
count lower than the store's counters. The portable reader is verified on PostgreSQL, and on SQLite through the test host of `Stratara.Testing.Orleans`; on PostgreSQL the
native reader is the one to use. A store that already holds entries is positioned once, after migrating and before the first
start, with `PartitionCounterBackfill.RunAsync`; the host refuses to start while an entry without a
position remains. Running the backfill again changes nothing.

The backfill never moves a position it did not hand out: unpositioned entries take the positions after the
last one of their partition, so a checkpoint written before a backfill stays true and the reader resumes from
it. On a store nothing has appended to with the counter yet, that is the history in the order it was appended.
An entry a process appended without the counter **after** entries appended with it is read after those — a
later version of its own stream included. Stop the process that appends without the counter before running
the backfill; a read model that stops on the resulting order — a missing preceding fact — is repaired by
rebuilding it.

## Adopt the roles

Each role is one call after the composite the host already has. Roles may be split across silos: a silo
publishes the roles it registers, and the grains of a role are placed only on silos that publish it — see
[placement by role](operate-the-orleans-execution-model.md#placement-by-role). A silo that registers a role
registers everything of that role: every command handler with `AddStrataraAggregateGrains`, every
projection with `AddStrataraProjectionGrains`, every saga and process with `AddStrataraSagaGrains`, and the
timer owner check and handler with `AddStrataraDurableTimers`.

| Role today | Add after the composite | What changes |
|---|---|---|
| API or backend host (`AddBackendServices`) | `AddStrataraOrleansCommandDispatcher()` and `AddStrataraIntentStore<AppWriteDbContext>()` | `ICommandOutboxDispatcher` records the command in the outbox table and hands it to its activation instead of publishing it. Composes with `AddAuthorizingCommandOutboxDispatcher()` in either order |
| Command worker (`AddCommandWorkerServices`) | `builder.AddCommandServices()` instead, then `AddStrataraOrleansCommandDispatcher()`, `AddStrataraIntentStore<AppWriteDbContext>()` and `AddStrataraAggregateGrains()` | A command that names an aggregate runs in that aggregate's grain, and heavy commands run in pools on these silos. Register the aggregate grains after every other pipeline behaviour. The silo's own sends — from a handler or a saga — are recorded and handed over instead of published. The bus-fed mediator worker is not registered, so the silo consumes no command queue; keep `AddCommandWorkerServices` only while API hosts that still publish to the command topic remain. A silo that also registers a store-reading role needs no broker — see [when the broker can go](#when-the-broker-can-go); one without keeps publishing its bundles to the bus. Sends between aggregates must not form a cycle: a send back into an aggregate whose turn is waiting on the sender is refused at once, naming both |
| Outbox worker (`AddOutboxWorkerServices`) | `AddStrataraSingletonWork<OutboxDrainWork>(OutboxDrainWork.WorkName)` and `AddStrataraIntentStore<AppWriteDbContext>()` on the silos, and retire the worker host | The drain runs once per cluster and resumes the commands a crash left behind, whichever host recorded them; it resumes only where an intent store is registered and logs `LogEvents.Orleans.RecordedCommandsWithoutIntentStore` where one is missing. The drain silo reads `OrleansDispatchOptions.IntentGrace` and `MessageRetryOptions.MaxDeliveryAttempts` from its own configuration: give it the values the API host has, and the bus-envelope signer and integrity mode where the hosts sign. A backlog is resumed in passes that follow each other while they are full, not one batch per `PollingInterval`. Registered with its name, the drain is not constructed until the silo is active. The Redis outbox lock is no longer needed |
| Projection worker (`AddEventProjectionWorkerServices`) | `builder.AddEventProjectionServices()` instead, then `AddStrataraProjectionCheckpoints<AppReadDbContext>()` and `AddStrataraProjectionGrains()` | One grain per projection and partition reads the store from a checkpoint; the bus-fed worker is not registered |
| Saga worker (`AddSagaWorkerServices`) | `builder.AddSagaServices()` instead, then `AddStrataraSagaGrains()` | One grain per partition hands each fact to the sagas; stateful processes derive from `SagaProcess<TState>`. Processes own durable timers, so the silo runs a reminder service |
| Heavy command worker (`AddHeavyCommandWorkerServices`) | `ConfigureStrataraHeavyWork(o => o.ClusterWideLimit = …)` | Heavy commands run in a bounded pool per silo under cluster-wide permits; the heavy lane and its host go |
| Timeouts the host built itself | `AddStrataraDurableTimers()` with one `ITimerOwners` and one `ITimerHandler` | Owner-checked, durable, once per cluster; the host's ports may be registered before or after the model. Timer owners are placed only on silos that registered the ports; a silo that calls `AddStrataraDurableTimers` only to register timers hosts none |

Retire the bus outbox worker **before the first silo with an intent store runs the drain**, not only
before the first host records commands. The intent store still claims a stored bus command — one the bus
dispatcher wrote because a publish failed or a replay was active — once it is older than
`OrleansDispatchOptions.IntentGrace`, so a bus outbox worker running beside such a silo runs that command
on both paths. Since 4.1.1 a recorded command is stored under a kind of its own that no bus drain reads;
a command recorded under 4.1.0 still carries the bus command kind, so stop every bus outbox worker before
upgrading hosts that record commands, and keep it stopped until those records are gone.

A store reader applies what it reads under the session each entry was recorded under, and one read may return
entries of several tenants and users. A projection, a saga or a process — and every service it depends on — is
resolved after that session is set: consecutive entries recorded under one session share one scope, as one
bundle did on the bus, and an entry under another session gets a scope of its own. A dependency that takes the
tenant when it is constructed, such as a connection routed per tenant through `IDbResolver` or a read-model
context that captures the tenant, therefore sees the entry's tenant, never its neighbour's.

A process's timeout is the exception: a timer carries its owner and purpose, not a session. The process grain
reads the first entry of the process's state stream to find the session the process was started under, and it
reads that entry **before** any session is in place. A host that routes connections per tenant must let its
`IDbResolver` answer for an absent tenant with a connection that reaches the process state streams, or the
timeouts fail.

During a rollout a host can run both models at once — the bus consumer and the grains both apply
idempotently. `AddStrataraProjectionGrains` and `AddStrataraSagaGrains` take `hybrid: true` to keep
publishing bundles to the bus while the grains read the store.

Every registration of the model is idempotent in itself as well: a host whose feature modules or composites call
the same registration twice gets the composition one call gives — each projection woken once per bundle, each
singleton work started once. A singleton work is best registered with the name it runs under,
`AddStrataraSingletonWork<TWork>(name)`: the silo publishes that name without constructing the work, where the
overload without a name constructs every work while the silo starts, before any hosted service registered after
the silo has run. A work whose `Name` differs from the name it was registered with fails the start naming both.

To test the roles on the execution model before a cluster exists, run them in the test's process with
`ExecutionModelTestHost` from `Stratara.Testing.Orleans`, registered with the same calls — see
[On the Orleans execution model](testing-patterns.md#on-the-orleans-execution-model).

### When the broker can go

A host stops needing the message broker when the execution model has replaced both of the bus dispatchers it
uses: the command dispatcher, by `AddStrataraOrleansCommandDispatcher` with an intent store, and the bundle
dispatcher, by a store-reading role — `AddStrataraProjectionGrains` or `AddStrataraSagaGrains` without
`hybrid` — on the same host. Then nothing on the host publishes to or consumes from the bus, and the bus the
composites register is never connected; the connection string and the `Messaging` section may go.

| Host | Composed as | Broker |
|---|---|---|
| API host | `AddBackendServices`, `AddStrataraOrleansCommandDispatcher`, `AddStrataraIntentStore` | not needed — it records commands and commits nothing itself |
| Command silo with a store-reading role | `AddCommandServices`, the dispatcher and intent store, `AddStrataraAggregateGrains`, and `AddEventProjectionServices` with `AddStrataraProjectionGrains` or `AddSagaServices` with `AddStrataraSagaGrains` | not needed |
| Command silo without a store-reading role | `AddCommandServices`, the dispatcher and intent store, `AddStrataraAggregateGrains` | **needed** — the facts its handlers commit are published as bundles to the bus, because it has no reader to wake |
| Any host on a `*WorkerServices` composite | the bus-fed worker is registered | needed |

A command silo without a store-reading role keeps the broker until it registers one; the readers on other
silos read the store either way, so the bundles it publishes serve only the bus consumers that remain.

## After the cut-over: the bus queues

The bus workers own one durable queue per subscription, named after `IMessagingIdentifier`: the command
subscription (`command-subscription` by default), the heavy-command subscription
(`heavy-command-subscription`), and the two event-bundle subscriptions of the projection and saga workers
(`event-bundle-subscription`, `event-bundle-saga-subscription`). On RabbitMQ each is the quorum queue
`<subscription>.v2` with `<subscription>.dead-letter` beside it — see the
[RabbitMQ outbox setup](outbox-setup-rabbitmq.md); on Azure Service Bus each is a subscription of the topic
with its dead-letter sub-queue. Retire them in this order, which loses nothing:

1. **Retire every publisher to the exchange first.** For the command and heavy-command topics, every host
   that dispatches runs the execution model's dispatcher; for the event-bundle topic, no host runs a worker
   composite that publishes bundles and no silo keeps `hybrid: true` or lacks a store-reading role. A
   publication to an exchange whose queues are gone is returned by the broker, stored in the outbox table,
   and retried by the outbox drain for ever — every bundle of such a host accumulates there.
2. **Stop the last consumer of the queue and let it drain** until its depth is zero.
3. **Look at the dead-letter queue before emptying it.** A message there is a command or a bundle the
   bus workers gave up on — the bus-side counterpart of a kept command in the intent store. Decide for each
   whether it is re-issued through the execution model or dropped.
4. **Delete the queue and its dead-letter queue**, in the broker's management interface or with its
   command-line tool.
5. **Keep a queue for as long as a consumer outside this deployment subscribes to the exchange**, and keep
   `hybrid: true` on the silos for exactly that long.

The Redis key of the bus outbox worker's lock needs no cleanup; it expires on its own.

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
| `CommitOrderOptions` | `PartitionCount` 16 |
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
