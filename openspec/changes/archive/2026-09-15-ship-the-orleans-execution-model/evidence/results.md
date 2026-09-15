# Results

> **Status:** complete, 2026-09-15 — performance (7.3) and correctness (7.4) recorded. Every number cites its raw
> directory under `evidence/raw/` or its workflow run.
> The comparison is with the archived numbers of `optimise-the-orleans-execution-model`
> (`openspec/changes/archive/2026-09-14-optimise-the-orleans-execution-model/evidence/results.md`), the run
> with the built-in directory for aggregate grains, which is what the packages register.

## Code under measurement

The packaged projects `Stratara.Orleans` and `Stratara.Orleans.EntityFrameworkCore` at commit `c8eb95e` on
`feature/ship-the-orleans-execution-model`, built in Release; the B3 run after the lease fix measures `e38c50c`
plus that fix, which is committed together with this file. Production profile for reminders, the built-in grain
directory for aggregate and runner grains (`PocSiloDirectory.BuiltInDefault`), Redis for the grains that select
the durable directory. The benchmark executable is `tests/Stratara.Orleans.Benchmarks`, which since task 7.1 hosts
the scenarios of `tests/Stratara.Orleans.Scenarios`.

## Performance (task 7.3)

Expectation (design D14): within 10 % of the archived numbers, or explained.

| | Measurement | Archived (built-in directory) | Now | Against archived | Verdict | Raw |
|---|---|---|---|---|---|---|
| B3 | Commands per aggregate, medians of 3 × 2 000 at 2000×1 / 20×100 / 1×2000, handler appends to the aggregate's stream, one process | bus 584 / 560 / 322; grain-intent 1 452 / 1 359 / 309; grain-sync 1 395 / 1 912 / 334 commands/s; 0 conflicts | First run: bus 600 / 552 / 329; grain-intent 1 281 / 1 199 / 268; grain-sync 1 271 / 1 868 / 344. After the lease fix: bus 463 / 512 / 327; grain-intent 1 389 / 1 268 / 303; grain-sync 1 260 / 1 969 / 317. **After the review fixes (`8dd1d1c`, commands accepted into the aggregate's order, D27):** bus 487 / 506 / 321; **grain-intent 1 443 / 1 320 / 316**; grain-sync 1 243 / 1 857 / 316 commands/s; 0 conflicts on every path in every run | First run: grain-intent −12 / −12 / −13 %. After the lease fix: grain-intent −4 / −7 / −2 %. After the review fixes: **grain-intent −1 / −3 / +2 %**; grain-sync −11 / −3 / −5 %; bus −17 / −10 / 0 % | **holds.** The intent shape is within 3 % of the archived numbers. The synchronous shape's −11 % on 2000×1 is explained by the run rather than the path: the unchanged bus control ran 17 % below its archived number in the same run, and against that control the synchronous shape is at 255 % where the archive had 239 %. The extra call a fresh activation makes to run its queue may account for part of it; that share is not measured | `raw/commands-per-aggregate/20260915-114629/` (first), `20260915-122442/` (after the lease fix), `20260915-143202/` (after the review fixes) |
| B5 | Resource use, 60 s idle then 60 s at 200 commands/s, one host each, fresh aggregate per command | bus 171 / 188 MB, 2.26 CPU-s per 1 000; silo 186 / 210 MB, 0.031 idle CPU-s per s, 2.53 CPU-s per 1 000 = +12 % | bus 167 / 185 MB, 0.008 idle CPU-s per s, 2.27 CPU-s per 1 000; silo **190 / 217 MB, 0.028 idle CPU-s per s, 2.44 CPU-s per 1 000 = +7 %** of the bus host; 12 000 of 12 000 applied on both | silo +2 / +4 % memory, −10 % idle processor time, −4 % per 1 000 commands; bus +0 % per 1 000 | **holds** | `raw/resources/20260915-114914/` |

### The durable-intent shape in B3

Against the bus worker of the same run the intent shape is at 213 / 217 / 81 %, archived 249 / 243 / 96 %. The
bus control moved by at most 3 %, so the loss is the intent path's own. Since the archived run the path gained,
among other things, the full mediator pipeline in the grain for a recorded command (task 4.5), the renewed
hand-over lease and the failure bookkeeping of the bounded resume (group 3), and options validation and timer-port
resolution that do not run per command.

**Measured (2026-09-15, `raw/intent-path-profile/20260915-121012/`).** The grain-intent path alone, 3 × 2 000
commands per distribution, built-in directory, with temporary switches that were removed afterwards (not in any
commit). Medians, commands/s at 2000×1 / 20×100 / 1×2000:

| Variant | Medians | Against the two controls (1 153 / 1 188 / 268) |
|---|---|---|
| Control, as shipped (run twice) | 1 149 / 1 228 / 269 and 1 157 / 1 147 / 266 | — |
| Without the renewal when the lease starts | 1 235 / 1 253 / 311 | +7 / +5 / +16 % |
| Without the lease (no renewal, no renewal timer) | 1 248 / 1 267 / 318 | +8 / +7 / +19 % |
| Handler invoked directly instead of through the mediator | 1 120 / 1 129 / 267 | −3 / −5 / 0 % |
| Without the lease and without the mediator | 1 224 / 1 205 / 302 | +6 / +1 / +13 % |

The cost is the **renewal when the lease starts**: one statement against the store per command, on a context of
its own, inside the aggregate's turn. On one aggregate the turn is the bottleneck and the statement costs the whole
difference (1×2000: 318 without the lease against 309 archived). The renewal timer is within the noise, and the
mediator pipeline costs nothing measurable. In this run the intent path is the first in a fresh process, so the
first repetition of 2000×1 is cold (699–861) and the medians there are not comparable with the full B3 run, where
the bus path ran first.

**The fix.** A command handed over moments after it was recorded no longer renews its hand-over when execution
starts: its record time keeps it out of the drain until the lease's renewals take over. The lease reads the record
time from the time-ordered intent id and renews at the start only when the command is older than a threshold — a
command that waited, and every resumed one. The first version renewed at half the grace and skipped the start
renewal below a quarter of it; the pull-request review made a single failed renewal survivable, so the lease now
renews every third of the grace and skips the start renewal below a sixth, and since the review fixes (D27) it
starts when the aggregate accepts the command rather than when the command runs. `IntentLeaseTests` covers the
decision; `DurableIntentTests` (7/7, including a handler that runs longer than the grace and runs once, and a resume
that keeps its aggregate's order) holds. The B3 run after the first version, `raw/commands-per-aggregate/20260915-122442/`,
put the durable-intent shape within 4–7 % of the archived numbers; the run after the review fixes,
`raw/commands-per-aggregate/20260915-143202/`, within 3 %.

## Correctness (task 7.4)

`tests/Stratara.Orleans.IntegrationTests` on the packaged projects, run by `.github/workflows/integration.yml` via
`workflow_dispatch` on `feature/ship-the-orleans-execution-model` at commit `fdb5c8e` (the lease fix included),
three runs in a row on the hosted Ubuntu runner:

| Run | Duration | Orleans integration tests | RabbitMQ integration tests |
|---|---|---|---|
| `34969157789` | 24m37s | 56 / 56 | 42 / 42 |
| `34971671299` | 25m36s | 56 / 56 | 42 / 42 |
| `34974373060` | 24m37s | 56 / 56 | 42 / 42 |
| `34982913485`, at `8cd105e` after the review fixes | 25m36s | 57 / 57 (with `SelfDispatchTests`) | 42 / 42 |

The archived suite had 37 Orleans tests. The 19 added cases cover:
- the mediator pipeline on the intent path, and a command a handler sends for another aggregate;
- the commit-order readers' model names and the partition-counter backfill;
- the start check for the storage-backed directory;
- the replay and the single-projection rebuild with store readers;
- reentrant timers;
- placement of singleton work;
- the two-silo kills: singleton takeover, and a timer registered on the killed silo firing once.

The kill tests run with due times from a start signal and assert that the kill lands before them.

Locally, before the runs: `HardKillTimerTests`, `OwnerCheckedTimerTests`, `TwoSiloKillTests` and
`SingletonWorkTests` 6 / 6 together; `DurableIntentTests` 7 / 7 after the lease fix; the Orleans unit tests
79 / 79.
