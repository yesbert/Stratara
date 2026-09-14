# Operations note — running the Orleans execution model

> Written 2026-09-14 from the measurements in `results.md`. It complements the archived
> `migration-note.md` of `prove-an-orleans-execution-model`, which says what a host registers;
> this one says what an operator has to know. A change that ships the execution model carries
> both into the documentation site.

## A silo that dies hard, and a replacement on another address

**The problem.** Orleans keeps a membership table of silos. A silo that is killed — no graceful
stop — leaves its row marked *Active*. A new silo that starts on a **different** address must, before
it may join, reach every Active silo in the table. It cannot reach the dead one, retries for
`MaxJoinAttemptTime` (five minutes by default), and then fails with
`OrleansClusterConnectivityCheckFailedException`. In Orleans 10.3.1 no `IAmAlive` setting changes
this: the join never skips a stale entry (`MembershipAgent.ValidateInitialConnectivity`, source of
`v10.3.1`). Measured in `raw/restart-delay/20260914-101351/`: 420 s to failure under Orleans'
defaults and under shortened `IAmAlive` settings alike.

This is the shape an orchestrator produces when it replaces a crashed pod with a new one that gets a
new address, and the cluster had **one** silo.

**What works.**

1. **Run two silos.** A surviving active silo votes the dead one out after its missed probes —
   about thirty to forty-five seconds with the defaults — and a new silo joins as soon as that is
   done. This is the answer Orleans is built around.
2. **Restart on the same address.** A silo that comes back on the address it had marks its older
   clone dead on start ("Detected older version of myself") and is ready in under a second. A
   reminder that was due while it was down fires on its due time after the restart. Measured:
   ready 0.7–0.9 s, timer fired 3.0–3.1 s after a restart, in every setting
   (`raw/restart-delay/20260914-091549/`). An orchestrator with stable network identities gives this
   for free.
3. **Clean the table.** Delete the dead silo's row from `orleansmembershiptable` (ADO.NET) before
   the replacement starts. The proof of concept's reset (`PocReset`) does this for a whole cluster;
   a shipped package would offer it per silo.

**What does not work.** Shortening `IAmAliveTablePublishTimeout` and `NumMissedTableIAmAliveLimit`.
They decide when a stale entry is logged and left out of gossip, not whether a joiner waits for it.
Keep Orleans' defaults.

**What a graceful stop costs.** 0.1–0.2 s for every host shape the proof of concept has, also for a
host that itself replaced a killed one (`raw/stop-delay/`). A host that is stopped rather than
killed leaves no problem behind.

## Which grain directory

The proof of concept's silo used Redis as the directory for every grain. Only the grains whose
single activation must survive an unstable cluster need that: the store readers (projections,
sagas), singleton work, timer owners, the process manager, the permit grain. They select it by name
(`GrainDirectories.Durable`). Aggregate and runner grains take the built-in directory: a duplicate
activation of an aggregate grain ends in a concurrency conflict at the store, which is the guarantee
the bus path has always relied on across processes.

Measured (`raw/resources/`): with the built-in directory for aggregate grains the silo host costs
2.53 CPU-s per 1 000 commands against the bus host's 2.26 (+12 %) and 186 MB idle; with Redis as the
default for everything, 2.81 (+27 %) and 227 MB. A shipped registration should default to the
built-in directory for aggregate grains and register the durable one for the rest.

## Which profile

A deployed silo runs Orleans' defaults for reminders and the outbox drain at its default interval.
The proof of concept's tests lower the minimum reminder period to one second and the reminder
refresh to five seconds so a test can observe a reminder; that profile costs idle processor time
and is not for production. The benchmarks were taken under the production profile.

## What the bus path costs since 4.0.4

The bus worker's queues became quorum queues in `dead-letter-what-a-handler-cannot-take` (#71) —
the price of the dead-letter guarantee. On this machine that took the worker from 851 to about 600
aggregate-scoped commands per second on spread aggregates and left it at about 320 on one aggregate
(`raw/commands-per-aggregate/`). A consumer comparing the two execution models compares against
this, not against the archived number.

## Still open

- The silo's idle processor time is 0.031 CPU-s per second where the bus host's is 0.007; a profile
  of an idle silo would name the periodic work behind it.
- The hint path's p99 sits between 8 and 25 ms depending on the run, the push's between 10 and
  16 ms; the median and p90 are ahead of the push in every run.
