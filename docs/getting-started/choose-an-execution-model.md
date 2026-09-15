---
title: "Choose an Execution Model"
description: "The bus workers or the Orleans execution model: which one runs your handlers, projections and sagas, and how to move from one to the other."
---

# Choose an Execution Model

> **Derived page.** The behaviour described here is specified by the `orleans-execution` and
> `host-composition` capabilities under `openspec/specs/`. Those specifications are the source; this
> page explains and illustrates them. Where the two disagree, the specification is right and this page
> is a bug.

Your handlers, projections and sagas are written once. What runs them is a choice you make per host,
and per role, without touching them.

## The two models

**The Orleans execution model — recommended.** Commands, projections, sagas, timers and singleton work
run as virtual actors on an Orleans cluster. One aggregate has one writer across the deployment, an
accepted command survives a crash, projections and sagas read the store in commit order so no committed
fact is missed, and work that must happen once per cluster needs no lock.
[Read what it guarantees](../concepts/orleans-execution-model.md).

**The bus workers — supported.** Commands and event bundles travel over RabbitMQ or Azure Service Bus,
and worker hosts consume them. A host that already runs a broker and the worker composites keeps
working as it does.

## Which one

| Your situation | Choose |
|---|---|
| A new deployment that can run a small cluster | The Orleans execution model |
| Two commands for one aggregate from different hosts must never conflict | The Orleans execution model |
| A projection or saga must never miss a committed fact after a crash | The Orleans execution model |
| Timeouts, reminders or once-per-cluster jobs | The Orleans execution model |
| An existing host on the bus workers that meets its needs | The bus workers, until a role needs one of the above |
| Other systems consume the same broker topics | The bus workers for those topics |
| A single process with no cluster and no broker | Neither — the mediator and the event store are enough |

## Moving between them

A host adopts the execution model one role at a time, with one registration after the composite it
already calls, and can run both models side by side during a rollout. Each role's call is in the
[migration guide](../guides/migrate-to-the-orleans-execution-model.md); what an operator has to know is in
[Operate the Orleans Execution Model](../guides/operate-the-orleans-execution-model.md).
