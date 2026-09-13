# Expectations — registered before the first run

> **Confirmed by the owner:** 2026-09-13, as written. No file under `evidence/raw/` existed before this
> line carried a date. Confirmed with two things known and left unchanged: Orleans promises no message
> order (T3 holds through the scoped send lane, see design D5), and a silo restarted on the same
> endpoint waits for its predecessor to be declared dead, so the kill-and-restart runs take several
> times the durations registered below (T5 took 23 minutes). T1 was approved at 20 kills per path with
> that in mind; the benchmarks were approved as registered.

Every measurement below states what it expects and what would falsify it. A number written after a run
is not a threshold; if a run shows that a threshold was badly chosen, the run stands as recorded and the
threshold is changed in a later commit that says why.

## Where the runs happen

| | |
|---|---|
| Machine | Apple M2 Ultra, 24 cores, 64 GB |
| Container runtime | Docker 29.7.2, 24 CPU, 47 GB |
| .NET SDK | 10.0.401 (`global.json`: 10.0.100, roll-forward latest feature) |
| Orleans | 10.3.1 for every Orleans package |
| Images | `postgres:17-alpine`, `redis:7-alpine`, `rabbitmq:4-management-alpine` |
| Commit | recorded per run |

Bus workers and silo run on this machine in the same session, against containers started by the run.
Nothing runs in the cloud and nothing costs money. No single run below is expected to exceed 30 minutes,
so no run needs the owner's go-ahead on duration; if one would, it is asked for first.

## What a raw result is

`evidence/raw/<measurement>/<yyyyMMdd-HHmmss>/` with:

- `environment.json` — commit, machine, OS, Docker version, image digests, package versions;
- `result.json` — every individual observation (per iteration, per sample), never only aggregates;
- `console.log` — the run's console output.

A run that was aborted is committed like any other, with the reason in `console.log`.

## Correctness — pass or fail

| # | Test | Iterations | Expected | Falsified if | Expected duration |
|---|---|---|---|---|---|
| T1 | Kill between commit and publish | 20 kills per path | bus path: every kill loses the bundle in flight; checkpoint path: every event is applied after restart | the checkpoint path misses one event in 20 kills | 5 min per path |
| T2 | Interleaved commits | 1 deterministic case + 200 randomised cases per reader | naive reader skips in the deterministic case; safety-window reader (100 ms) skips in the 2 s-hold case; portable and native readers skip nothing | a portable or native reader skips one entry; or the naive reader does not skip in the deterministic case, which means the test does not provoke the interleaving and is repaired before anything else runs | 2 min |
| T3 | Two quick commands, one aggregate | 500 pairs (approve, then cancel) | 500 of 500 applied in arrival order | one pair reordered | 1 min |
| T4 | Timer owner removed while due | 20 | handler runs against a missing owner: 0; timer rows left behind: 0 | one handler run against a missing owner, or one timer row left | 2 min |
| T5 | Hard kill with open timers, restart | 10 kills; per kill 5 kept and 5 removed owners | after restart, each kept owner's timer fires exactly once within two refresh periods; removed owners: 0 fires | one fire for a removed owner; or a kept owner with 0 or more than 1 fire | 5 min |
| T6 | Interactive commands under heavy burst | baseline 200 interactive commands without burst; then 200 interactive commands while 500 heavy items of 200 ms each drain | p99 under burst ≤ 2 × baseline p99, and ≤ baseline p99 + 100 ms | either bound exceeded | 3 min |
| T7 | Duplicate stream version on SQLite | 1 | `DbUpdateException` that is **not** `ConcurrencyException` | `ConcurrencyException` is raised — SF-003's impact is then withdrawn, and that is a result too | seconds |

Mechanisms that the numbers assume:

- **T1** kills a separately started host process from the outside (SIGKILL). The window is hit by a
  test-only bundle dispatcher registered *in the host under test* that terminates the process on its Nth
  call — it runs after the commit and before any publish, which is exactly the window. Nothing under
  `src/` outside `src/Stratara.Orleans/` changes for it.
- **T2** opens two database connections with explicit transactions: A inserts and holds, B inserts and
  commits, the reader reads, A commits, the reader reads again. The randomised cases vary hold times
  between 0 and 50 ms and the reader's timing.
- **T4/T5** lower `MinimumReminderPeriod` to 1 s and `RefreshReminderListPeriod` to 5 s in the test silo
  (Orleans accepts both with a warning). "Two refresh periods" in T5 is therefore 10 s.
- **T6** measures on the same silo, with the heavy grain bounded to 4 concurrent items.

## Performance — against the bus workers on the same machine

Each benchmark runs three repetitions; the median is compared, and all three are in `result.json`.

| # | Measurement | Shape | Hypothesis | Falsified if | Expected duration |
|---|---|---|---|---|---|
| B1 | Append throughput | 10 000 appends of one event each; writers 1, 8, 32; streams spread over all buckets, and all writers on one bucket; three store layouts: current, native (transaction-id column), portable counter | native ≥ 90 % of current at every setting; portable counter is the slowest layout on one bucket at 32 writers; portable counter on one bucket at 8 writers ≥ 200 appends/s (**the D10 usable-throughput threshold**); portable counter with spread buckets at 32 writers ≥ 50 % of current | native < 90 % of current at any setting; or either portable-counter bound missed — the second is a **stop criterion** | 10 min |
| B2 | Event to read model, p50 and p99 | 2 000 events at 50/s; latency from commit to the row being readable; paths: bus push, checkpoint catch-up woken by a grain-call hint, hybrid | push has the lowest p50; hint p50 ≤ 50 ms and p99 ≤ 250 ms; hybrid p99 ≤ push p99 | hint p99 > 250 ms — "close enough for interactive views" is then false | 3 min |
| B3 | Commands per aggregate | 2 000 aggregate-scoped commands; distributions: 2 000 aggregates × 1, 20 × 100, 1 × 2 000; bus = command worker at processor-count subscriptions, grain = aggregate grain | grain throughput ≥ 80 % of bus at 2 000 × 1, ≥ 100 % at 20 × 100 and 1 × 2 000; concurrency conflicts on the grain path: 0 | grain below the bound in any distribution; or one concurrency conflict on the grain path | 5 min |
| B4 | Rebuild duration | 100 000 events over three projections; full replay (truncate all, replay all) against a rebuild of one projection while the other two keep applying live events at 20/s | per-projection rebuild ≤ 60 % of the full replay's duration; the other two projections apply > 0 live events during it | per-projection rebuild > 100 % of the full replay; or 0 live events applied by the others during it | 15 min |
| B5 | Resource use | 60 s idle, then 60 s at 200 commands/s (B3's 2 000 × 1 shape); RSS and CPU sampled every second for the bus host and the silo host | silo idle RSS > bus idle RSS; silo CPU-seconds per 1 000 commands ≤ bus CPU-seconds per 1 000 commands | silo per-1 000 CPU > bus by more than 20 % | 5 min |

Total expected wall-clock time for one complete pass: about 60 minutes across separate runs.

## Stop criteria, with numbers

Work stops and is reported to the owner when any of these is observed:

1. **T1** — the checkpoint path misses an event and the cause is not understood within the session.
2. **B1** — the portable counter on one bucket at 8 writers stays below 200 appends/s, or with spread
   buckets at 32 writers below 50 % of the current store.
3. **Task 3.1** — Stratara and Orleans cannot be registered in one host without changing an existing
   composite.

## What is deliberately not measured

- SQL Server (task 4.5 is optional and last).
- Any cluster larger than two silos on one machine — the recommendation says so where it matters.
- Network partitions between silos; the grain directory's behaviour under them is Orleans's promise,
  not this proof of concept's.
