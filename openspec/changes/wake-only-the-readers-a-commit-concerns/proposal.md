# Wake only the readers a commit concerns

> **Status:** approved

## Why

On the Orleans execution model, every projection and every saga keeps one store reader per partition. A commit
wakes every one of those readers in each partition it touched, whatever event types it committed. Each woken
reader reads the new entries and writes its checkpoint, even when its consumer handles none of them. An idle
reader also polls the store on its own timer. The cost grows with the number of consumers times the partition
count, not with the work.

A consumer reported it on 2026-10-28 under 4.5.0 (consumer finding F-030). With 68 projections and sagas and 16
partitions, a silo runs 1,092 readers. Ten single-event commands caused 680 checkpoint writes, although one
projection handles that event. An idle silo ran about 650 store scans and 436 transactions a second. Their heavy
seeding job ran five times slower than on the bus model it replaced, and a larger connection pool did not help.

Verified on `main` at `f76fa6d` (4.6.0). The commit's wake-up names only partitions. A reader reads every entry of
its partition after its position and decides what applies only while applying. It then advances its checkpoint
past the whole batch, also when nothing in the batch applied.

## What Changes

The effect a consumer sees: a commit costs work only for the readers whose consumer handles one of its events.
An idle silo costs the store a few queries per poll interval rather than one per reader. Nothing is applied
differently, and no fact is missed.

- **A commit wakes the readers it concerns.** After a commit, the host wakes, in each partition the commit touched,
  only the readers whose projection or saga handles one of the committed event types. A stateful saga process,
  which decides from the event itself, is always woken. So is every reader when the silo cannot decide an event's
  relevance, such as an event type it does not know. This holds for the wake-up from a host that runs a
  store-reading role and for the store wake-ups a client host registers.
- **A projection's read that applied nothing does not write a checkpoint at once.** A projection's reader keeps the
  position past the entries it read and applied nothing for, and answers waits from it. It writes that position as
  its checkpoint with the next batch that applies something, after a new bounded interval, or when it deactivates.
  A reader started again after a crash re-reads at most those entries and applies nothing for them. The interval
  is a new option on the projection grain settings. A saga's reader keeps writing at once, because a saga added to
  a running deployment starts where the host's sagas have written they read, and must not react to anything before
  that.
- **An idle poll asks the store once per partition.** A reader's poll first asks whether its partition has
  anything past the reader's position. Each silo answers that from one head query per partition, shared by all its
  readers and at most as old as a part of the poll interval. A reader that is at the head does not read. A lost
  wake-up therefore still costs only latency, now up to a little over one poll interval rather than one.
- **Rolling deployment.** The wake-up through the cluster keeps its form, and the event types travel beside it. A
  wake-up from a host of the previous version names no event types and wakes every reader of the partition as before.
  A silo of the previous version ignores the types and wakes every reader too. Checkpoints keep their form, so either
  version reads the other's.
- **The in-process test host waits for the readers' positions.** `ExecutionModelTestHost.WaitForReadersAsync`
  compared the stored checkpoints with the head, so it would have waited out a checkpoint held back. It now asks
  each reader where it has read to. Found by the sample's smoke test during implementation.
- **Unchanged:** commit order and the guarantee that a committed fact reaches every projection and saga, the
  checkpoint's fencing, pauses, rebuilds and replays, seeding at the head, waiting for a projection, retirement,
  stalls and their metrics, the poll interval's option and default, and the bus path.
- **Documentation:** `docs/guides/operate-the-orleans-execution-model.md` says what a commit wakes, when a
  checkpoint is written and what a restart re-reads, and describes the new option. The XML documentation of the new
  option and of `PollInterval` says the same.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `test-support`: *A test can run the execution model in one process* says that a reader counts as having reached
  the head once it has read to it, whether or not it has written its checkpoint there.
- `orleans-execution`:
  - *Projections and sagas read the store in commit order and never miss a committed fact* changes from "a commit
    wakes the readers of the partitions it touched" to the readers of those partitions whose consumer handles one of
    the committed events. A lost wake-up is still covered by the poll, which now reads the store only when its
    partition has moved.
  - Gains *A commit costs work only for the readers it concerns*. It covers whom a commit wakes, when a reader
    writes the checkpoint past entries it applied nothing for, what a restart re-reads, what an idle poll costs, and
    the rolling deployment.

## Impact

- **Code:** `Stratara.Orleans`, which holds the commit's wake-up, the store wake-up grains, the store reader loop,
  the projection and saga reader grains and their options. One public member is added to
  `ProjectionGrainOptions`, the interval after which a deferred checkpoint is written. The
  wake-up grains keep their interface, and the event types travel in the call's request context, so a rolling
  deployment mixes silos and hosts of both versions. No other public type changes, and no stored form changes.
- **Test support:** `Stratara.Testing.Orleans`, where `WaitForReadersAsync` reads the readers' positions. No public
  member changes.
- **Tests:** `tests/Stratara.Orleans.IntegrationTests` on the PostgreSQL store with the native reader, and the
  portable reader where the poll's head check is concerned. A unit test in `tests/Stratara.Orleans.Tests` covers the
  relevance decision for a wake-up.
- **Documentation:** `docs/guides/operate-the-orleans-execution-model.md` and `CHANGELOG.md`.
- **Release:** a patch (4.6.1). The API addition is additive, and behaviour gets cheaper without changing what is
  applied.
- **Consumer finding:** NextPA `stratara-findings.md`, F-030. NextPA F-031 (a conflicting command waits for its
  hand-over to lapse) is a separate change.
- **Superseded sources:** none.
