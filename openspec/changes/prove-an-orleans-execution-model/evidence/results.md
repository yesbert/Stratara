# Results

> **Status:** complete, 2026-09-13. Every number cites its raw directory under `evidence/raw/`.
> Expectations are the ones the owner confirmed in `expectations.md`; no threshold was moved after a
> run. Three hypotheses were falsified and are recorded as such: B3 on one aggregate, B4's duration,
> B5's processor time.

## Correctness

| # | Test | Expected | Observed | Verdict | Evidence |
|---|---|---|---|---|---|
| T1 | Kill between commit and publish, 20 per path | bus loses every bundle in flight; checkpoint path loses none | bus path: 20 of 20 events never reached the read model after the restart; checkpoint path: 0 of 20 lost | **holds — SF-002 is closed by construction on the checkpoint path** | `raw/commit-publish-kill/20260913-152919/result.json`, 437 s |
| T2 | Interleaved commits, per reader | naive skips (fast and long hold); window skips under long hold; portable and native never | naive skipped in the fast, the long-hold and the reversed-id case; window skipped under the long hold only; portable counter and PostgreSQL transaction-id reader: 0 skips in 3 deterministic cases and 200 randomised each | **holds** | `InterleavedCommitTests`, 16 of 16, run of 2026-09-13 (log `run-8`) |
| T3 | Two quick commands, one aggregate, 500 pairs | 500 of 500 in arrival order | 500 of 500 on distinct aggregates and 500 pairs on one aggregate — **only with the scoped send lane**; without it 420 and 425 of 500 pairs reordered | **holds, with a finding**: Orleans promises no message order (design D5) | `ArrivalOrderTests`, 2 of 2 |
| T4 | Timer owner removed while due, 20 owners | 0 firings, 0 timers left | 0 firings, 0 timers left; the kept-owner control fired exactly once each, never early | **holds** | `OwnerCheckedTimerTests`, 2 of 2 |
| T5 | Hard kill with open timers, 10 kills, 5 kept + 5 removed each | kept fire once, removed never | 10 of 10 kills as expected | **holds** | `HardKillTimerTests`, 1 of 1, 1376 s |
| T6 | Interactive under heavy burst | burst p99 ≤ 2 × baseline p99 and ≤ baseline + 100 ms; heavy never above the cluster limit | 200 interactive commands alone: p50 0.6 ms, p99 1.6 ms; while 500 heavy units of 200 ms drained: p50 0.6 ms, p99 1.1 ms; heavy in use at most 4 of the limit of 4 | **holds** | `raw/heavy-burst/20260913-152934/result.json` |
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
| B1 | Append throughput, medians of 3 × 10 000 appends | native ≥ 90 % of current; counter on one bucket at 8 writers ≥ 200/s and spread at 32 writers ≥ 50 % of current | current store: 588 / 2 612 / 2 632 appends/s spread at 1 / 8 / 32 writers, 384 / 1 720 / 1 871 on one bucket. **Native: 100 / 102 / 99 % and 99 / 99 / 97 % of current.** Portable counter: 57 / 54 / 56 % spread, 67 / 32 / 26 % on one bucket — 548/s at 8 writers on one bucket, 489/s at 32, the slowest layout there as predicted | **holds** on every bound; the counter's cost is a halving, not a collapse, and 56 % clears the 50 % bound narrowly | `raw/append-throughput/20260913-152935/result.json` |
| B2 | Event to read model, 3 × 2 000 events at 50/s, clock started before the commit | push lowest p50; hint p50 ≤ 50 ms, p99 ≤ 250 ms; hybrid p99 ≤ push p99 | bus push: p50 2.9–3.0 ms, p99 9.0–10.8 ms. Checkpoint catch-up with hint: p50 3.2 ms, p99 12.3–16.6 ms. Hybrid: p50 2.6–2.7 ms, p99 6.0–6.9 ms | **holds** — the hint path is 0.2 ms behind the push at p50 and well inside the interactive bounds; the hybrid is the fastest of all because whichever path arrives first wins | `raw/read-model-latency/` (run of 15:42) |
| B3 | Commands per aggregate, medians of 3 × 2 000 commands, handler appends to the aggregate's stream, one process | grain ≥ 80 % of bus at 2000×1, ≥ 100 % at 20×100 and 1×2000; 0 conflicts on the grain path | bus worker: 851 / 832 / 326 commands/s at 2000×1 / 20×100 / 1×2000. Grain, durable intent: 1 111 / 1 248 / 284 = **131 / 150 / 87 %**. Grain, synchronous: 1 411 / 1 855 / 324 = **166 / 223 / 99 %**. Conflicts: 0 on every path | **holds at 2000×1 and 20×100; falsified at 1×2000** — on one aggregate everything is serial on both paths, the grain adds a hop and the intent shape an outbox row per command. The "no requeue storm" half is untested here: one process never storms | `raw/commands-per-aggregate/20260913-154844/result.json` |
| B4 | Rebuild, 100 000 events over 20 000 streams and 3 projections, live events at 20/s during it | per-projection rebuild ≤ 60 % of the full replay; others apply live events during it | full replay (bus): 80.5 s; 1 363 live events reached the untouched audit projection only when the replay got to them — p50 39.6 s, p99 78.5 s. Per-projection rebuild (grain): 75.4 s = **94 %**; 1 308 live events reached the audit projection at p50 3 ms, p99 17 ms | **half holds, half does not**: the others keep applying, decisively; the duration is 94 %, not ≤ 60 % — reading and mapping 100 000 entries dominates, applying one projection instead of three saves little. Below the falsification bound of 100 %, above the hypothesis | `raw/rebuild/` (run of 15:51) |
| B5 | Resource use, 60 s idle then 60 s at 200 commands/s, one host each | silo idle RSS higher; silo CPU per 1 000 commands ≤ bus (falsified above +20 %) | bus-worker host: 172 MB idle, 187 MB under load, 12 000 of 12 000 applied, **2.54 CPU-s per 1 000 commands**. Silo host with the durable-intent dispatcher: 231 MB idle, 264 MB under load, 12 000 of 12 000 applied, **4.04 CPU-s per 1 000** | **first half holds, second half falsified**: the silo costs 59 % more processor time per command, not less — the outbox row per intent, the grain hop and the drain are paid on every command | `raw/resources/20260913-155549/result.json` |

## Deviations from the expectations, recorded before interpretation

- **Durations.** Every run that kills and restarts a silo on the same endpoint waits for the
  predecessor to be declared dead — one to two minutes per restart. T5 took 23 minutes against a
  registered 5; the intent and saga kill tests took 22 and 6. The counts were not reduced.
- **T3's mechanism.** The hypothesis assumed the grain's turn orders arrivals. It does not; Orleans
  promises no message order. The observed 500 of 500 rests on the scoped send lane the proof of
  concept added, and the recommendation must say so.

## Stop criteria

None triggered: the checkpoint path lost no event in T1 or T2; the portable counter reached 548
appends/s on one bucket at 8 writers against a bound of 200 (B1); Stratara and Orleans registered in
one host in both orders without a change to any existing composite (task 3.1).

## Recommendation

**Offer Orleans as an additional execution model. Do not make it the recommended one, and do not
deprecate the bus workers.** The evidence for each half:

*What the Orleans path does that the bus path cannot, and the numbers behind it:*

- A committed fact reaches every projection and saga, whatever dies in between — T1: 0 of 20 lost
  against 20 of 20. This closes SF-002 for consumers on the checkpoint path, by construction.
- Commit order is readable without losing a late committer — T2, both candidate readers, 0 skips in
  406 interleavings each, including the reversed-transaction-id case; the native reader at no cost to
  the store (B1, 97–102 %).
- Owner-checked durable timers, once per cluster, surviving a kill — T4 and T5, 0 wrong firings in
  30 owners.
- One projection rebuilt while the others keep serving live events at 3 ms instead of 40 s — B4,
  second half.
- Interactive latency untouched by a heavy burst behind a cluster-wide bound — T6.
- Read-model latency on par with the push — B2, 3.2 against 3.0 ms at p50.
- Better throughput where commands spread over aggregates — B3, 131–223 % of the bus worker.

*What argues against making it the default:*

- It costs more to run: 59 % more processor time per command and 35 % more resident memory idle —
  B5, falsified against its own hypothesis.
- It is not faster where a consumer would most hope: one aggregate under load is 87–99 % of the bus
  worker (B3), and a rebuild of one projection takes 94 % of a full replay (B4), because reading and
  mapping the store dominates.
- It adds operational surface a bus deployment does not have: a clustering table and a reminder
  table with scripts the packages do not ship, a Redis grain directory, and a restart-on-the-same-
  endpoint delay of one to two minutes before reminders resume.
- It does not order messages. The turn serialises; the order a caller sees is the one the scoped
  send lane enforces — which is a promise of Stratara's, not of Orleans's, and it holds within one
  scope only. The hand-off assumed more.
- Its strongest guarantees are ones a consumer needs only sometimes: a process manager with
  timeouts, a singleton without a lock, a projection that must never miss a fact. A consumer that
  publishes commands from an API and reads its views a moment later gets nothing it did not have.

*Therefore:* ship the Orleans path as its own package family for consumers who need what it
guarantees, with the migration note as the entry, and keep the bus workers as a supported,
undeprecated execution model. Revisit the recommendation when a consumer has run the Orleans path in
production for a release, and when the two costs that decided this — processor time per command and
the restart delay — have been measured there rather than here.

*Answers to the open questions the recommendation settles:*

- **Q5, version pinning.** The Orleans packages are pinned to one version in
  `Directory.Packages.props`; a shipped package family would depend on `[10.3.1, 11.0.0)` and treat an
  Orleans minor upgrade as a Stratara patch unless the wire format changes. A consumer that hosts its
  own Orleans must run inside that range — that is a constraint the migration note states.
- **Q6, the bus workers.** Kept, supported, not deprecated. Removal is not scheduled for the next
  major; the question is asked again after the first production release of the Orleans path.
