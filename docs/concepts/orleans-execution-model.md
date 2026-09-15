---
title: "The Orleans Execution Model"
description: "Commands, projections, sagas, timers and singleton work as virtual actors on an Orleans cluster: what the model guarantees, what it costs, and what stays as it is."
---

# The Orleans Execution Model

> **Derived page.** The behaviour described here is specified by the `orleans-execution` and
> `host-composition` capabilities under `openspec/specs/`. Those specifications are the source; this
> page explains and illustrates them. Where the two disagree, the specification is right and this page
> is a bug.

Stratara runs the same handlers, projections and sagas in two ways. The bus workers take commands and
event bundles from RabbitMQ or Azure Service Bus. The Orleans execution model runs them as virtual
actors on an Orleans cluster, so that a committed fact is never lost to a crash, one aggregate has one
writer across the whole deployment, and work that must happen once per cluster needs no lock.

It ships as two packages: `Stratara.Orleans`, the runtime, and `Stratara.Orleans.EntityFrameworkCore`,
the persistence it needs. Both depend on Orleans 10.3.1 or a later 10.x release.

## What the model guarantees

**One writer per aggregate.** A command that names an aggregate runs in that aggregate's activation,
and two commands for the same aggregate never run at the same time anywhere in the cluster. A command
a handler sends for another aggregate runs in that aggregate's activation. If an unstable cluster
activates an aggregate twice anyway, the store's version constraint refuses the second writer, so the
guarantee never falls below the one the bus workers give.

**An accepted command is not lost.** The execution model's command dispatcher records a command
durably before the dispatch returns and hands it to its activation afterwards. A host that dies in
between is resumed after a grace. A command whose handler keeps failing is resumed a bounded number of
times — the same bound the host configures for bus messages — and then kept for an operator, without
holding back the commands after it. Every command on this path passes the same mediator pipeline as on
the bus: validation, authorization, tenant isolation, audit.

**No committed fact is missed.** Projections and sagas read the event store from a checkpoint, in commit
order. A commit wakes the readers of the partitions it touched; a lost wake-up costs latency, never a
fact, because the readers poll the store as well. A projection can be rebuilt on its own while every
other projection keeps applying live events.

**A failure is visible, not skipped.** An entry that cannot be applied stops its partition: the
checkpoint stays before it, the failure is logged with the entry's identity, the stall is counted, and
the entry is tried again.

**Once per cluster.** Singleton work runs in one place in the cluster, only on silos that registered
it, and moves to another silo when its silo is lost. Durable timers belong to an owner, fire once on or
after their due time, never fire for an owner that is gone, and survive a restart. A stateful process's
timeout survives a kill at any point of the step that scheduled it.

**Bounded heavy work.** Heavy commands run in a bounded pool per silo and under a cluster-wide number of
permits; a permit whose silo died is released, and interactive commands do not queue behind heavy work.

**A clean slate on demand.** A host can clear everything the model keeps beside the event stream —
reminders, membership, the grain directory, checkpoints — and the event stream is never touched.

## What it costs

- **A cluster to run.** Silos need a storage-backed membership table, a reminder service and a
  storage-backed grain directory. See [Operate the Orleans Execution Model](../guides/operate-the-orleans-execution-model.md).
- **A failing entry holds back its partition.** Nothing after it in that partition advances until it
  passes. On the bus a failed bundle is dead-lettered and the stream moves on.
- **At least once, still.** A handler that completed but whose completion was not recorded before a
  crash runs again. Handlers stay idempotent, as they are under the bus.
- **A single silo that dies hard.** A replacement on another network address does not join while the
  dead silo's membership entry is active. The operations page names the three ways out.

## What stays as it is

`ICommandHandler<TCommand>`, `IProjection`, `ISaga`, `IAggregateScopedCommand`, `IHeavyCommand`,
`IEventSource`, the promise of `ICommandOutboxDispatcher`, the outbox table and the event stream. A host
adopts the model one role at a time, after the composite it already calls, and can run both models at
once during a rollout.

## Where to go next

- [Choose an Execution Model](../getting-started/choose-an-execution-model.md) — which model for which host.
- [Migrate to the Orleans Execution Model](../guides/migrate-to-the-orleans-execution-model.md) — what a host adds, role by role.
- [Operate the Orleans Execution Model](../guides/operate-the-orleans-execution-model.md) — what an operator has to know.
- [DI Extensions Cheatsheet](../reference/di-extensions-cheatsheet.md) — every registration, under *Orleans execution model*.
