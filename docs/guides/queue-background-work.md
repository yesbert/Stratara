---
title: "Queue Background Work"
description: "Hand work off to run after the request returns: an in-process queue that gives you an identifier, runs the work in its own scope on several workers, and tells you how it went."
---

# Queue Background Work

> **Derived page.** The behaviour described here is specified by the `host-composition` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Some work should not hold up the request that asked for it: rebuilding a report, warming a cache,
cleaning up after an import. `IBackgroundTaskQueue` takes that work, returns at once with an
identifier, and runs the work shortly afterwards on a background worker in the same process.

The queue lives in memory. Work still waiting when the process stops is not run, and nothing about it
survives a restart. Work that has to happen — or has to happen in another process — belongs on the
outbox (`ICommandOutboxDispatcher`) rather than here.

## Register the queue

```csharp
builder.Services.AddBackgroundTasks();
```

`AddBackgroundTasks()` (package `Stratara.Infrastructure`) registers the queue as a singleton and the
hosted service that drains it. Neither the worker composites nor `AddBackendServices()` call it for
you — a host that wants the queue registers it. There is one queue per host.

## Work can be queued for in-process execution

A unit of work is a delegate that receives a service provider and a cancellation token. Queuing it
with `QueueTaskAsync` gives you back a `Guid` that identifies it; the work itself runs later, on a
background worker.

```csharp
using Stratara.Abstractions.BackgroundTasks;

public sealed class ReportRequests(IBackgroundTaskQueue queue)
{
    public ValueTask<Guid> RequestAsync(Guid reportId) =>
        queue.QueueTaskAsync(async (scopedServices, cancellationToken) =>
        {
            var logger = scopedServices.GetRequiredService<ILogger<ReportRequests>>();
            logger.LogInformation("Building report {ReportId}", reportId);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        });
}
```

```csharp
builder.Services.AddScoped<ReportRequests>();

app.MapPost("/reports/{reportId:guid}", async (Guid reportId, ReportRequests reports) =>
    Results.Accepted($"/tasks/{await reports.RequestAsync(reportId)}"));
```

Three things hold for every item:

- **You get an identifier, not a result.** `QueueTaskAsync` completes once the item is in the queue.
  What the work produces is the work's business — write it somewhere the caller can read it later.
- **A full queue makes the caller wait.** The queue holds at most 100 pending items. When it is full,
  `QueueTaskAsync` does not complete until a worker has taken an item out; nothing is discarded and
  the queue does not grow past its capacity. Await the call — a burst of producers slows down rather
  than losing work.
- **Each item runs in a fresh dependency-injection scope.** The service provider the delegate
  receives belongs to a scope created for that item and disposed after it. Resolve scoped services —
  a database context, the unit of work — from it. Do not capture scoped services from the request
  that queued the work: that scope is gone by the time the work runs.

## Queued work reports its own outcome

`GetTaskInfo` returns a `BackgroundTaskInfo` for an identifier the queue handed out. Its `Status` is a
`BackgroundTaskStatus`:

| Status | When |
|---|---|
| `Queued` | The item is waiting for a worker |
| `Running` | A worker has taken the item and the delegate is executing |
| `Completed` | The delegate returned normally |
| `Failed` | The delegate threw. `Error` carries the exception's message |

```csharp
app.MapGet("/tasks/{taskId:guid}", (Guid taskId, IBackgroundTaskQueue queue) =>
    queue.GetTaskInfo(taskId) is { } info
        ? Results.Ok(new { info.Status, info.Error })
        : Results.NotFound());
```

A failure belongs to the item that threw. The worker records it against that item, logs the exception
(event `101_003`), and carries on with the next item — one failing delegate does not stop background
processing for the host.

For an identifier the queue does not know, `GetTaskInfo` returns `null`. It does not invent a status.

`DequeueAsync` and `UpdateTaskStatus` are also on the interface. They are what the background worker
calls; application code queues and reads, and leaves those two alone.

## Status retention is bounded

The queue keeps the status of the most recent 10 000 items. When an item is queued past that limit,
the oldest status record is discarded, so a host that runs for months holds the same amount of
status in memory as one that has just started.

- While fewer items than the limit have been queued, every status record is still there.
- Once the limit is exceeded, the oldest records go first and the most recent are kept. Asking for a
  discarded item returns `null`, exactly as for an identifier the queue never issued.

Read an outcome while it is recent. A caller that has to know how an item ended long afterwards
should have the work record its own result rather than rely on the queue's memory.

## Background execution is parallel and ordered on entry

Items are taken up in the order they were queued. The hosted service runs one worker per processor
on the machine, and each worker takes the next item as soon as it is free — so several items run at
the same time, and a slow item does not hold up the ones behind it.

Order on entry is not order of completion. Two items queued one after the other start in that order
but may finish in either. Work that must follow other work belongs in the same delegate, or is queued
by the work it depends on when that work finishes.

When the host shuts down, the workers stop taking new items and the cancellation token each running
delegate received is signalled. The shutdown is logged (event `101_004`), as the start was
(`101_001`); a successful item is logged at debug level (`101_002`). The ranges are listed in the
**[LogEvents Schema](../reference/log-events-schema.md)**.

## See also

- **[DI Composition](../getting-started/di-composition.md)** — which registrations a host needs.
- **[DI Extensions Cheatsheet](../reference/di-extensions-cheatsheet.md)** — `AddBackgroundTasks()`
  beside the other à-la-carte registrations.
- **[Outbox — RabbitMQ](outbox-setup-rabbitmq.md)** — for work that must survive a restart or run in
  another process.
