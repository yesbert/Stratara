# Close the round-5 execution gaps

> **Status:** approved (owner, 2026-09-16 — recorded at the owner's request)

## Why

A second readiness audit of the Orleans execution model on 2026-09-16, run before application teams
build on it and after #102–#106, traced ten defects in code that reach every deployment, and a set of
documentation errors a team following the migration guide would act on. None is covered by a test;
the integration suite is green at 67/67 because no test crosses the runtime's response timeout, sends
in a cycle, splits roles across silos, rebuilds twice, or resets against tables in a schema.

- **A long queue on one aggregate abandons itself.** The aggregate grain runs its accepted queue
  through an ordinary request to itself. A queue that takes longer than the runtime's response
  timeout (30 s by default — one 40-second handler, or a hundred half-second commands) faults that
  request while the runner keeps going; the grain reads the fault as "the run never started", fails
  every waiting forwarded command back to its caller with *the activation ended*, and drops the
  lease of every waiting recorded command so the drain resumes it a grace later, out of order. The
  same timeout reports a forwarded command whose handler outlasts it as failed to its caller although
  it commits.
- **A send cycle between two aggregates stands still.** A handler in aggregate A that sends to B
  while B's handler sends to A waits in A's queue behind A's own running turn, which waits on B.
  Nothing moves until the response timeout, then both fail. On the bus path the same handlers ran.
- **"However long it runs" holds only for handlers that yield.** The lease and permit renewals run
  on the activation's scheduler; a heavy handler that computes without awaiting past the grace is
  never renewed, is claimed by the drain and runs a second time in another worker while the first is
  still running. A heavy hand-over is also not leased while it waits in the worker pool's queue, so a
  burst longer than the grace hands the same command over twice.
- **A failing handler on the recorded path is silent** until the command is kept, attempts × grace
  later; a hand-over that fails is not logged at all.
- **Two rebuilds of one projection, or a rebuild during a replay, empty its read model for good.** The
  pause is a flag, not a count; the first rebuild's resume lets the readers advance the checkpoint
  past the rows the second rebuild then deletes.
- **In a cluster whose silos register different roles — the topology the migration guide's role table
  describes — aggregates, projections, sagas, timer owners and heavy work activate on silos that lack
  the role**, throw *not registered on this silo*, and stay there; a timer owner placed so throws on
  every tick and is never unregistered. Only singleton work is placed by role today.
- **The reset can report success having removed nothing:** it looks the runtime tables up by
  unqualified name and treats an absent table as empty, so tables in a schema are never touched; and
  the documented call resolves it as a scoped service from the root provider, which throws wherever
  scope validation is on.
- A partition whose *read* fails is not counted as stalled, and a batch cancelled mid-way never
  writes the checkpoint for what it applied.

## What Changes

- **The aggregate's queue runner is a one-way request**, so no response timeout applies to it; a
  forwarded command is not reported failed while its handler runs.
- **A send that would close a cycle between aggregates fails at once** with a message naming the
  sending and the receiving aggregate, instead of waiting for the response timeout; the chain of
  aggregates a turn is inside travels with each grain call. The specification states that sends
  between aggregates form a directed acyclic graph.
- **Renewals run off the activation's scheduler**, so a handler that never yields is still renewed,
  and **a heavy hand-over is leased from its acceptance**, before it waits for a worker or a permit.
- **Every failed attempt of a recorded command, and every failed hand-over, is logged** with the
  command's identity and attempt; two new log events under the Orleans band.
- **A projection's readers resume only when the last pauser resumes**, and a rebuild requested while a
  full replay is active is refused with a message.
- **Every role is placed by role.** A silo publishes in its metadata the roles it registered —
  aggregate commands, projections, sagas, durable timers, heavy work — and the grains of a role are
  placed only on silos that publish it, as singleton work already is. A call for a role no silo
  publishes fails naming the role. The API host may join the cluster as an Orleans client, which the
  documentation and a test show.
- **The reset fails when a runtime table is absent** instead of counting zero, takes the schema the
  tables live in, and the documentation resolves it from a scope.
- **A read that fails counts as a stall** and is logged on every path; a cancelled batch still writes
  the checkpoint for the entries it applied.
- **Documentation:** the guide names the three Orleans provider packages the shown composition needs
  and where their SQL scripts are; the trigger for stopping the bus outbox worker is the first drain
  silo with an intent store, because the intent store still claims bus-kind records older than the
  grace; the `Stratara.Orleans` README quick start uses the projection composite without the bus
  worker; the saga role's need for a reminder service and the drain's own retry bound are stated;
  the response timeout is named as a setting the host sizes; the log events 117_001–117_113 and the
  `orleans.*` instruments are listed on the reference page and named in the operations guide with
  what to alert on; the two pages that still deny a checkpoint store are corrected.
- **Consumer-visible effects:** a send cycle now fails immediately rather than after the timeout
  (an exception where there was a `TimeoutException`); a host whose silos publish no role a grain
  needs sees a failure naming the role where it saw *not registered on this silo*; a second
  overlapping rebuild waits for the first instead of interleaving; a reset against absent tables
  throws where it reported zero. No schema change. New public surface: a schema option on the reset
  registration, and the two log event ids. Versioning: patch.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`:
  - *One aggregate has one writer across the cluster* — sends between aggregates are acyclic, and a
    send that would close a cycle is refused at once; new scenario.
  - *An accepted command is recorded before the call returns and resumed after a crash* — "however
    long it runs" is stated to hold for a handler that does not yield and for a heavy command waiting
    for a worker; a failed attempt is logged; a queue longer than the response timeout runs to the
    end; new scenarios.
  - *Projections and sagas read the store in commit order and never miss a committed fact* — two
    rebuilds of one projection do not interleave, and a rebuild during a replay is refused; new
    scenario.
  - *A failing entry stops its partition, is retried, and is visible* — a read that fails is a stall.
  - *A host can reset what the execution model keeps outside the event stream* — an absent runtime
    table fails the reset; the schema is the host's to name.
  - *The execution model can be adopted per role beside the bus workers* — a role's grains are placed
    only on silos that registered the role; a cluster without such a silo fails naming the role; the
    API host may be a client; new scenarios.

## Impact

- `Stratara.Orleans` — `AggregateGrain` (one-way runner, cycle chain), `AggregateGrainBehavior`,
  `AggregateSendLane`, `IntentLease` and `HeavyWorkGrain` (renewals, accept-then-run),
  `OrleansCommandDispatcher` (hand-over logging), `ProjectionGrain`/`ProjectionRebuilder`/
  `ReplayCheckpointReset` (pause count, replay refusal), `StoreReaderLoop` (read failure, cancelled
  checkpoint), the role registrations and a placement filter per role beside
  `SingletonWorkPlacement`, `OrleansLog`.
- `Stratara.Orleans.EntityFrameworkCore` — `ExecutionModelReset`, `AddStrataraExecutionModelReset`
  (schema option).
- `Stratara.Diagnostics` — two log event ids in `LogEvents.Orleans`.
- `docs/guides/migrate-to-the-orleans-execution-model.md`, `docs/guides/operate-the-orleans-execution-model.md`,
  `docs/concepts/orleans-execution-model.md`, `docs/reference/log-events-schema.md`,
  `docs/guides/observe-the-framework.md`, `docs/guides/write-a-projection.md`,
  `src/Stratara.Orleans/README.md`, `src/Stratara.Orleans.EntityFrameworkCore/README.md`, `llms.txt`.
- Tests: a queue past the response timeout; a send cycle; a non-yielding heavy handler past the
  grace; a heavy burst asserting single execution; overlapping rebuilds; a role-split cluster per
  role and an API client; a reset against tables in a schema and against absent tables; a failed
  read counted as a stall.
- Versioning: patch (4.1.2).
- Out of scope, proposed as their own change: starting the store readers at the head of a populated
  read store, and the transaction-id column's migration on a populated event table.
