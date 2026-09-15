---
title: "Operate the Orleans Execution Model"
description: "What an operator of the Orleans execution model has to know: a silo that dies hard, the grain directory, kept commands, stalled partitions, the reset, and the reminder profile."
---

# Operate the Orleans Execution Model

> **Derived page.** The behaviour described here is specified by the `orleans-execution` capability
> under `openspec/specs/`. That specification is the source; this page explains and illustrates it.
> Where the two disagree, the specification is right and this page is a bug.

[Migrate to the Orleans Execution Model](migrate-to-the-orleans-execution-model.md) says what a host
registers. This page says what an operator has to know once it runs.

## A silo that dies hard, and a replacement on another address

Orleans keeps a membership table of silos. A silo that is killed — no graceful stop — leaves its row
marked active. A new silo that starts on a **different** network address must reach every active silo
in the table before it may join. It cannot reach the dead one, retries for `MaxJoinAttemptTime` (five
minutes by default), and then fails with `OrleansClusterConnectivityCheckFailedException`. Shortening
the `IAmAlive` settings does not change this: they decide when a stale entry is logged, not whether a
joiner waits for it.

This is the shape an orchestrator produces when it replaces a crashed pod that was the cluster's **only**
silo with a new pod on a new address. There are three ways out:

1. **Run two silos.** A surviving active silo votes the dead one out after its missed probes, and the
   replacement joins as soon as that is done. This is the answer Orleans is built around.
2. **Restart on the same address.** A silo that comes back on the address it had declares its older
   self dead on start and is ready at once, and a timer that came due while it was down fires. An
   orchestrator with stable network identities gives this for free.
3. **Clean the membership table.** Delete the dead silo's row from the membership table before the
   replacement starts, or run the [reset](#reset-what-the-model-keeps) while no silo of the cluster runs.

A silo that is stopped rather than killed leaves nothing behind.

## The grain directory

The grains whose single activation must survive an unstable cluster — the store readers of projections
and sagas, singleton work, timer owners, stateful processes and the heavy-work permits — are placed in
the storage-backed directory the host registers with `AddStrataraOrleans`. Aggregate grains and command
runners use the runtime's built-in directory: a second activation of an aggregate ends in a concurrency
conflict at the store, which is the guarantee the bus workers have always relied on.

With Redis:

```csharp
var redis = StackExchange.Redis.ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("redis")!);

builder.UseOrleans(silo => silo
    .AddStrataraOrleans((s, name) => s.AddRedisGrainDirectory(name, options => options.ConfigurationOptions = redis)));
```

A silo without a directory under that name fails at start and names the call.

## Kept commands

A recorded command whose handler keeps failing is resumed up to `MessageRetryOptions.MaxDeliveryAttempts`
times and then kept: it stays in the outbox table with its attempt count and its last failure, and the
commands after it are still resumed. Find the kept commands with:

```sql
SELECT id, aggregate_id, attempt_count, last_failure
FROM outbox_entry
WHERE kept_at IS NOT NULL;
```

Once the cause is fixed, return a kept command; it is resumed with its attempts starting over:

```sql
UPDATE outbox_entry
SET kept_at = NULL, attempt_count = 0
WHERE id = @id AND kept_at IS NOT NULL;
```

## A partition that stops advancing

An entry a projection or saga cannot apply stops its partition. The checkpoint stays before the entry,
the failure is logged with the entry's identity and counted, and the entry is tried again on every wake-up
and poll — nothing after it in that partition advances until it passes. The event identifiers are listed
in the [log events schema](../reference/log-events-schema.md). A missing prerequisite from another
partition is retried under the preceding-fact policy in the same way.

## Reset what the model keeps

`IExecutionModelReset` clears everything the model keeps beside the event stream: the reminders of the
host's service, and with them every durable timer; the membership rows of its cluster; every checkpoint;
and the grain directory's entries, through a callback the host supplies because the directory is its
choice. The event stream is never touched, and a host started afterwards rebuilds every checkpoint from it.

```csharp
var orleansDb = builder.Configuration.GetConnectionString("orleans")!;
builder.Services.AddStrataraExecutionModelReset<AppReadDbContext>(
    orleansDb,
    async (services, cancellationToken) =>
    {
        // With the Redis directory: remove the cluster's keys and report how many.
        var multiplexer = services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
        var keys = multiplexer.GetServer(multiplexer.GetEndPoints()[0]).Keys(pattern: "*my-cluster*").ToArray();
        return keys.Length == 0 ? 0 : await multiplexer.GetDatabase().KeyDeleteAsync(keys);
    });

var report = await app.Services.GetRequiredService<IExecutionModelReset>().ResetAsync();
```

Run it while no silo of the cluster runs: a running silo writes its membership and reminders back.

## Reminder profile and clocks

A deployed silo runs Orleans' reminder defaults. Keep-alive periods and the timer retry period are kept
as reminders, so they must be at least the runtime's minimum reminder period; the host fails at start
otherwise. Lowering `ReminderOptions.MinimumReminderPeriod` is for tests that need to observe a reminder
within seconds.

A timer's due time is computed on the host that registered it and its tick runs on a silo with a clock of
its own. A tick earlier than the due time by less than `DurableTimerOptions.DueTolerance` fires instead of
waiting a whole retry period.
