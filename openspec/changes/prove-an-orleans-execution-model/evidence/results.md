# Results

> **Status:** in progress, 2026-09-13. Rows are filled in as runs complete; every number cites its
> raw directory under `evidence/raw/`. Expectations are the ones the owner confirmed in
> `expectations.md`; a threshold was never moved after a run.

## Correctness

| # | Test | Expected | Observed | Verdict | Evidence |
|---|---|---|---|---|---|
| T1 | Kill between commit and publish, 20 per path | bus loses every bundle in flight; checkpoint path loses none | _pending_ | | `raw/commit-publish-kill/` |
| T2 | Interleaved commits, per reader | naive skips (fast and long hold); window skips under long hold; portable and native never | naive skipped in the fast, the long-hold and the reversed-id case; window skipped under the long hold only; portable counter and PostgreSQL transaction-id reader: 0 skips in 3 deterministic cases and 200 randomised each | **holds** | `InterleavedCommitTests`, 16 of 16, run of 2026-09-13 (log `run-8`) |
| T3 | Two quick commands, one aggregate, 500 pairs | 500 of 500 in arrival order | 500 of 500 on distinct aggregates and 500 pairs on one aggregate — **only with the scoped send lane**; without it 420 and 425 of 500 pairs reordered | **holds, with a finding**: Orleans promises no message order (design D5) | `ArrivalOrderTests`, 2 of 2 |
| T4 | Timer owner removed while due, 20 owners | 0 firings, 0 timers left | 0 firings, 0 timers left; the kept-owner control fired exactly once each, never early | **holds** | `OwnerCheckedTimerTests`, 2 of 2 |
| T5 | Hard kill with open timers, 10 kills, 5 kept + 5 removed each | kept fire once, removed never | 10 of 10 kills as expected | **holds** | `HardKillTimerTests`, 1 of 1, 1376 s |
| T6 | Interactive under heavy burst | burst p99 ≤ 2 × baseline p99 and ≤ baseline + 100 ms; heavy never above the cluster limit | _pending (recorded run)_; the development run passed | | `raw/heavy-burst/` |
| T7 | Duplicate stream version on SQLite | `DbUpdateException`, not `ConcurrencyException` | `DbUpdateException` over a UNIQUE violation | **holds — SF-003 confirmed** | `EventSourceSqliteConcurrencyTests`, `tests/Stratara.Infrastructure.Tests` |

Also verified, outside the table: the durable-intent shape resumes a command and a heavy unit whose
host was killed between accepting and applying (5 kills each, `DurableIntentTests`); a stateful
process's timeout survives a kill (3 kills, `SagaProcessTimeoutTests`); an unchanged saga sees facts
in order under the recorded session (`SagaGrainTests`); a projection grain keeps idempotent apply,
stops on a genuine failure and retries a missing prerequisite without advancing (`ProjectionGrainTests`);
two silos run singleton work once per period (`SingletonWorkTests`); one reset empties reminders,
membership, directory and checkpoints (`ResetTests`); the composite and the silo register in either
order (`CoHostingTests`).

## Performance

| # | Measurement | Hypothesis | Observed | Verdict | Evidence |
|---|---|---|---|---|---|
| B1 | Append throughput | native ≥ 90 % of current; counter on one bucket at 8 writers ≥ 200/s and spread at 32 writers ≥ 50 % of current | _pending_ | | `raw/append-throughput/` |
| B2 | Event to read model, p50/p99 | push lowest p50; hint p50 ≤ 50 ms, p99 ≤ 250 ms; hybrid p99 ≤ push p99 | _pending_ | | `raw/read-model-latency/` |
| B3 | Commands per aggregate | grain ≥ 80 % of bus at 2000×1, ≥ 100 % at 20×100 and 1×2000; 0 conflicts on the grain path | _pending_ | | `raw/commands-per-aggregate/` |
| B4 | Rebuild duration | per-projection rebuild ≤ 60 % of the full replay; others apply live events during it | _pending_ | | `raw/rebuild/` |
| B5 | Resource use | silo idle RSS higher; silo CPU per 1 000 commands ≤ bus | _pending_ | | `raw/resources/` |

## Deviations from the expectations, recorded before interpretation

- **Durations.** Every run that kills and restarts a silo on the same endpoint waits for the
  predecessor to be declared dead — one to two minutes per restart. T5 took 23 minutes against a
  registered 5; the intent and saga kill tests took 22 and 6. The counts were not reduced.
- **T3's mechanism.** The hypothesis assumed the grain's turn orders arrivals. It does not; Orleans
  promises no message order. The observed 500 of 500 rests on the scoped send lane the proof of
  concept added, and the recommendation must say so.

## Stop criteria

None triggered: the checkpoint path lost no event in T2; the portable counter's throughput is
measured in B1 (pending); Stratara and Orleans registered in one host in both orders without a
change to any existing composite.

## Recommendation

_Written once B1–B5 are in (task 14.2)._
