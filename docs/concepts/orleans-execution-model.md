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
a handler sends for another aggregate runs in that aggregate's activation; sends between aggregates form
a directed acyclic graph, and a send that would close a cycle is refused at once, naming both aggregates,
rather than waiting for a timeout. If an unstable cluster
activates an aggregate twice anyway, the store's version constraint refuses the second writer, so the
guarantee never falls below the one the bus workers give. Heavy commands are the exception: they run in
their own bounded pool beside the aggregate's other commands, keep no order with them, and where both
append the version constraint refuses the later one, which is resumed like any failing command.

**An accepted command is not lost.** The execution model's command dispatcher records a command
durably before the dispatch returns and hands it to its activation afterwards. A host that dies in
between is resumed after a grace. A command whose handler keeps failing is resumed a bounded number of
times — the same bound the host configures for bus messages — and then kept for an operator, without
holding back the commands after it; every attempt that fails is logged. A command whose handler is still
running is never handed over twice — however long it runs, whether or not it yields, and while a heavy
command waits for a worker. The record is committed on its own, before the dispatch returns, and not with
anything the caller writes: a caller that needs its own writes and the command together saves first and
dispatches after. Every command on this path passes the same mediator pipeline as on
the bus: validation, authorization, tenant isolation, audit.

**No committed fact is missed.** Projections and sagas read the event store from a checkpoint, in commit
order — each projection and each saga with a checkpoint of its own. A commit wakes the readers of the
partitions it touched; a lost wake-up costs latency, never a fact, because the readers poll the store as
well. A projection can be rebuilt on its own while every other projection keeps applying live events. A saga
registered later starts where the deployment's sagas have read, so it does not run its side effects for the
store's history.

**A failure is visible, not skipped.** An entry that cannot be applied stops its partition for the
projection or saga that failed on it: its checkpoint stays before the entry, the failure is logged with the
entry's identity, the stall is counted under its name, and the entry is tried again. Every other projection
and saga applies the entry once and goes on. A read that fails counts as a stall too. Two rebuilds of one projection never
interleave, and a rebuild during a full replay is refused.

**Once per cluster.** Singleton work runs in one place in the cluster while the cluster agrees on its
membership, only on silos that registered it, and moves to another silo when its silo is lost; a silo declared
dead that is still running may run it once more before it learns of the declaration, so a work tolerates an
overlapping run. Durable timers belong to an owner, fire once on or
after their due time, never fire for an owner that is gone, and survive a restart. A stateful process's
timeout survives a kill at any point of the step that scheduled it.

**Bounded heavy work.** Heavy commands run in a bounded pool per silo and under a cluster-wide number of
permits; a permit whose silo died is released, the bound holds across the loss of the silo keeping the
permits, and interactive commands do not queue behind heavy work. The pool's units run beside each other
on workers of the silo, so a handler that computes without awaiting anything occupies one worker and
neither stops the silo's other units nor delays the next heavy command's acceptance.

**A clean slate on demand.** A host can clear everything the model keeps beside the event stream for its
own deployment — reminders, membership, the grain directory, the checkpoints of the projections and sagas
it registers — and the event stream is never touched.

## What it costs

- **A cluster to run.** Silos need a storage-backed membership table, a reminder service and a
  storage-backed grain directory. See [Operate the Orleans Execution Model](../guides/operate-the-orleans-execution-model.md).
- **A failing entry holds back its partition.** Nothing after it in that partition advances for the
  projection or saga that fails on it until it passes, and it is retried without a bound. On the bus a failed
  bundle is dead-lettered and the stream moves on.
- **At least once, still.** A handler that completed but whose completion was not recorded before a
  crash runs again. Handlers stay idempotent, as they are under the bus.
- **A timeout is not a failure.** A forwarded command whose handler outlasts the response timeout fails its
  caller with a timeout and commits all the same; a caller does not retry on it.
- **A full replay reads the store twice.** Store-reading projections apply the store once in the replay and
  once more from their checkpoints at the beginning, correctly because they apply idempotently; a single
  projection's rebuild re-reads once.
- **Authorization answers from the session.** A resumed command is authorized on a silo from the session
  recorded with it, so a provider that reads the current web request refuses it.
- **A single silo that dies hard.** A replacement on another network address does not join while the
  dead silo's membership entry is active. The operations page names the three ways out.

## What stays as it is

`ICommandHandler<TCommand>`, `IProjection`, `ISaga`, `IAggregateScopedCommand`, `IHeavyCommand`,
`IEventSource`, the promise of `ICommandOutboxDispatcher`, the outbox table and the event stream. A host
adopts the model one role at a time, after the composite it already calls, and can run both models at
once during a rollout. Roles may be split across silos: the grains of a role are placed only on silos
that registered it, and a host that only dispatches commands may join as an Orleans client.

## Where to go next

- [Choose an Execution Model](../getting-started/choose-an-execution-model.md) — which model for which host.
- [Migrate to the Orleans Execution Model](../guides/migrate-to-the-orleans-execution-model.md) — what a host adds, role by role.
- [Operate the Orleans Execution Model](../guides/operate-the-orleans-execution-model.md) — what an operator has to know.
- [DI Extensions Cheatsheet](../reference/di-extensions-cheatsheet.md) — every registration, under *Orleans execution model*.
