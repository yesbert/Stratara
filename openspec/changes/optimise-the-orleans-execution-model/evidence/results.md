# Results

> **Status:** complete, 2026-09-14. Every number cites its raw directory under `evidence/raw/`.
> Expectations are the ones the owner confirmed in `expectations.md`; no threshold was moved after a
> run. The archived numbers are those of
> `archive/2026-09-13-prove-an-orleans-execution-model/evidence/results.md`, measured on the same
> machine the day before.

## Code under measurement

The runs measure commit `ab78b62` (D1–D8 implemented). The first two rebuild runs started before
that commit existed and record `7da1ffd` in their `environment.json`; the working tree they ran
was byte-identical to `ab78b62` for every file under `src/` and `tests/` — the commit was made
while the second run was in progress and no source file changed in between.

## Performance — grain path against the bus path, same run

| # | Measurement | Archived (2026-09-13) | Today | Expectation | Verdict | Evidence |
|---|---|---|---|---|---|---|
| B4 | Rebuild of one projection against the full replay, 100 000 events over 20 000 streams and 3 projections, live events at 20/s | full replay 80.5 s; rebuild 75.4 s = **94 %**; live events at the untouched projection p50 3 ms, p99 17 ms | full replay 80.2 s; rebuild **14.1 s = 17.6 %**; 229 of 229 live events applied at p50 5 ms, p99 16 ms | ≤ 40 %; live p99 ≤ 50 ms | **holds** — the sixteen partitions now rebuild in parallel; the optional partition index (task 2.4) is not needed | `raw/rebuild/20260914-083800/` (production profile). `raw/rebuild/20260914-083337/` is the aborted first attempt: its bus half stands at 81.4 s, its grain half never started — see `ABORTED.md` there |
| B2 | Event to read model, 3 × 2 000 events at 50/s, clock started before the commit | bus push p50 2.9–3.0 / p99 9.0–10.8 ms; hint p50 3.2 / p99 12.3–16.6; hybrid p50 2.6–2.7 / p99 6.0–6.9 | bus push p50 3.2–3.4 / p99 10.5–15.3 (median 13.0); **hint p50 2.3–2.4 / p99 7.9, 18.5, 25.2 (median 18.5)**; hybrid p50 2.2 / p99 5.2–8.1 (median 5.5) | hint p50 ≤ push p50 + 0.2 ms; hint p99 ≤ 10 ms and ≤ push p99 + 1 ms | **p50 holds, p99 falsified**: the hint path is now 0.9 ms *ahead* of the push at p50 and at p90 (2.8–4.3 against 4.0–4.4 ms), but its p99 across the three repetitions is 7.9, 18.5 and 25.2 ms — one repetition inside the bound, two outside, with the bus at 10.5–15.3. The outliers on both paths come in runs of consecutive events (indices 44–49, 746–748 on the grain path; 176–181, 1122–1128 on the bus path), which is a stall — a collection, a store flush — and not a queue. The second B2 run below says whether the spread is the path's or the day's | `raw/read-model-latency/20260914-084229/` (production profile) |
| B3 | Commands per aggregate, medians of 3 × 2 000, handler appends to the aggregate's stream, one process | bus 851 / 832 / 326 commands/s at 2000×1 / 20×100 / 1×2000; grain-intent 1 111 / 1 248 / 284 = **131 / 150 / 87 %**; grain-sync 1 411 / 1 855 / 324 = 166 / 223 / 99 %; 0 conflicts | **Redis as default directory:** bus 598 / 557 / 319; grain-intent **1 369 / 1 359 / 315 = 229 / 244 / 99 %**; grain-sync 1 384 / 1 922 / 339 = 231 / 345 / 106 %. **Built-in directory for aggregate grains:** bus 584 / 560 / 322; grain-intent 1 452 / 1 359 / 309 = 249 / 243 / 96 %; grain-sync 1 395 / 1 912 / 334 = 239 / 341 / 104 %. 0 conflicts on every path and both settings | intent ≥ 130 / 140 / 95 %; sync within ±10 points; 0 conflicts | **holds** — but read the ratio with its control: the bus worker itself fell from 851 to about 600 commands/s at 2000×1 since the archived run, because `dead-letter-what-a-handler-cannot-take` (#71, merged after that run) moved the worker queues to quorum queues. Against its own archived absolute number the intent shape gained 23 / 9 / 11 % (the completion left the turn); the synchronous shape moved −2 / +4 / +5 %, which is noise, as its expectation said. The directory setting is worth about 6 % at 2000×1 for the intent shape and nothing elsewhere: in one silo the Redis round trip is local | `raw/commands-per-aggregate/20260914-085131/` (Redis as default), `raw/commands-per-aggregate/20260914-085411/` (built-in), production profile. `raw/commands-per-aggregate/20260914-084905/` is the aborted first attempt: the intent host's service registration was rejected; its bus numbers stand, see `ABORTED.md`; `20260914-085011/` is the built-in run stopped by hand right after it |
| B5 | Resource use, 60 s idle then 60 s at 200 commands/s, one host each, fresh aggregate per command | bus 172 MB idle / 187 MB load, 0.008 idle CPU-s per s, **2.54 CPU-s per 1 000**; silo 231 / 264 MB, 0.056 idle CPU-s per s, **4.04 CPU-s per 1 000 = +59 %** | **Redis as default directory:** bus 171 / 187 MB, 0.007 idle, **2.22 per 1 000**; silo 227 / 265 MB, 0.031 idle, **2.81 per 1 000 = +27 %**. **Built-in directory for aggregate grains:** bus 171 / 188 MB, 2.26 per 1 000; silo **186 / 210 MB**, 0.031 idle, **2.53 per 1 000 = +12 %**. 12 000 of 12 000 applied in every run | ≤ 3.0 per 1 000 and ≤ +20 % of the bus host; idle ≤ 0.02 CPU-s per s; idle RSS ≤ 231 MB | **holds with the built-in directory for aggregate grains** (+12 %, 186 MB idle), **holds on the absolute bound and misses the relative one with Redis as default** (+27 %, inside the +30 % falsification bound). Idle processor time fell from 0.056 to 0.031 CPU-s per second under the production profile and is still above the 0.02 registered — the remainder is the silo's own housekeeping, not the proof of concept's. A fresh aggregate per command is the worst case for the directory: every command is an activation, and with Redis as the default that is a registration per command; the built-in directory takes 0.28 CPU-s per 1 000 and 40 MB of resident memory off that | `raw/resources/20260914-085652/` (Redis as default), `raw/resources/20260914-090100/` (built-in), production profile |
| B2, second run | as above | as above | bus push p50 2.9–3.1 / p99 13.3–16.4 (median 15.0); **hint p50 2.1–2.2 / p99 8.0, 9.6, 12.0 (median 9.6)**; hybrid p50 2.2 / p99 5.5–6.4 (median 6.0) | as above | **holds in this run**: p50 0.9 ms ahead of the push, p99 median 9.6 ms, 5.4 ms below the push's. Taken together the two runs say: the hint path's median and p90 are consistently below the push's; its p99 sits between 8 and 25 ms and the push's between 10 and 16 ms, and which one is lower on a given run is decided by stalls both paths share. The expectation as registered holds in one run of two; it is recorded as **not settled**, not as falsified twice | `raw/read-model-latency/20260914-090721/` (production profile) |

## Restart delay

| # | Measurement | Archived | Today | Expectation | Verdict | Evidence |
|---|---|---|---|---|---|---|
| R1 | Single silo killed and restarted on the same endpoint; per restart the time until the host is ready and until a durable timer registered before the kill, due in 3 s, has fired; 3 restarts under each of Test/Default, Production/Default, Production/ShortIAmAlive | "one to two minutes per restart" (the archived reading of T5's 23 minutes for 10 kills) | **ready 0.7–0.8 s after every restart, timer fired 3.0–3.1 s after the restart — on its due time — under all three settings**; cold start 0.7–0.9 s. The silo logs "Detected older version of myself - Marking other older clone as Dead" on start | default: 60–120 s recorded; shortened: ≤ 30 s | **the premise is falsified, in the proof of concept's favour**: there is no membership delay on a same-endpoint restart in Orleans 10.3.1 — the newer epoch marks the older clone dead at once — and the reminder fires on time. The shortened `IAmAlive` settings change nothing here and are not recommended on this evidence. What the archived T5 spent its 23 minutes on is measured in R2 | `raw/restart-delay/20260914-091549/`. Two aborted attempts before it: `20260914-090509/` (intent host, Default only: ready 1.3–1.5 s, first command applied 1.6 s — the join and the first grain call, measured before the reshaping) and `20260914-091432/` (all numbers as in the final run; failed only when writing a NaN) |
| R2 | `HardKillTimerTests` under the shortened membership profile (`POC_MEMBERSHIP=ShortIAmAlive`), 10 kills, 5 kept and 5 removed owners each | 10 of 10 under the defaults, **23 minutes** | **10 of 10 as expected in 174.6 s**: every kept owner fired exactly once, no removed owner fired, every restart took 0.7–0.8 s and every iteration 16.6 s — of which 15 s is the settle the test waits by design. No silo was declared dead while alive: a false vote ends the silo ("I have been told I am dead") and the test would have failed | 10 of 10; no false death vote | **holds**. With R1 this also answers where the archived 23 minutes went: not into membership. The archived run's own report carries no per-kill timing; today's does, and the per-kill cost is the settle, not the restart. Whatever slowed the archived run — the machine, Docker, a 30-second graceful stop hitting its timeout on each of the ten hosts — it was not the silo's join, and the shortened `IAmAlive` settings are neither needed nor recommended on this evidence | `raw/hard-kill-timers-short/20260914-092010/` (test profile for reminders, shortened membership) |
| R1, second shape | A silo joining on **another** endpoint while a killed silo's entry is still Active, one join per setting, production profile, six-minute window | the archived "one to two minutes" read against this shape | **the join fails under both settings**: `OrleansClusterConnectivityCheckFailedException` after `MaxJoinAttemptTime` (five minutes, 420 s to the host's exit) — "Failed to get ping responses from 1 of 1 active silos" — with Orleans' defaults and with the shortened `IAmAlive` settings alike | default 60–120 s; shortened ≤ 30 s | **falsified for both settings, and the premise with them**: in 10.3.1 `MembershipAgent.ValidateInitialConnectivity` probes every Active entry until `MaxJoinAttemptTime` and never skips a stale one (source read 2026-09-14, `v10.3.1`); the `IAmAlive` staleness only excludes an entry from gossip and marks it for monitoring, and a joining silo casts no vote that lands within the window. A cluster whose only other Active entry belongs to a killed silo is therefore joinable only by a restart on the killed silo's own endpoint, by a second *active* silo that votes it dead, or by cleaning the table. That is the operational item for the recommendation — not a setting, and not fixable inside the proof of concept | `raw/restart-delay/20260914-101351/` (both joins with the joiner's log in `console.log`); `raw/restart-delay/20260914-093904/` is the same shape at the two-minute window, three joins per setting, all timed out; the same-endpoint numbers of both runs repeat `20260914-091549` |

## Correctness — every integration test of the archived change on the final code

| What | Result | Evidence |
|---|---|---|
| **The whole suite on the final code, one process, one cluster per test silo, Orleans' membership defaults** | **37 of 37 passed in 1 083.9 s** — T1 (0 of 20 lost on the checkpoint path), T2, T3 (500 of 500 in order), T4, T5 (10 of 10, restart 0.7 s per kill), T6, `DurableIntentTests`, `SagaProcessTimeoutTests`, `SagaGrainTests`, `ProjectionGrainTests`, `SingletonWorkTests`, `ResetTests`, `CoHostingTests` and the rest, every assertion as it was | `raw/integration-suite/20260914-104800/` |
| The whole suite before the clusters were separated, one process, in the archived order | **36 of 37 passed in 1 216.6 s** (the archived suite took hours). The one failure is `ProjectionGrainTests.A_genuine_failure_stops_the_checkpoint_until_it_is_gone`, and it failed before its silo was up: `ValidateInitialConnectivity` against `S127.0.0.1:11221`, the Active entry of a `SagaProcessTimeoutTests` host whose graceful stop the harness cut short with a kill after thirty seconds. Nothing the test asserts ran | `raw/integration-suite/20260914-094500/console.log`, lines 23310–23366 and 24733 |
| The failed class alone, fresh containers | **3 of 3 in 12.3 s** | `raw/integration-suite/20260914-094500/projection-grain-tests-rerun.log` |
| The first attempt at the suite, under Orleans' default membership settings | stopped after the same test failed the same way against the same stale entry | `raw/integration-suite/20260914-092500/` |
| `ProjectionGrainTests` and `SagaGrainTests` right after D1–D4, before any benchmark | 4 of 4 | run of 2026-09-14 10:22, in the session log |
| `tests/Stratara.Orleans.Tests` | 5 of 5: the surface test, the rebuilder's concurrent resume, the completion queue's three bounds | `dotnet tests/Stratara.Orleans.Tests/bin/Release/net10.0/Stratara.Orleans.Tests.dll` |

No assertion was changed. One test changed *how* it resets a checkpoint — `ProjectionGrainTests` now pauses the grain, resets, resumes — because a grain keeps its position (D4) and the rebuilder is the sanctioned writer of a checkpoint behind a grain; what it asserts, idempotent re-apply from a reset checkpoint, is the same. The suite's one failure is the join shape of R1, met inside the test cluster: a killed silo's Active entry blocks every later joiner on another endpoint, and the kill tests leave one behind whenever a graceful stop outlives the harness's patience — R3 measures that stop.
| R3 | Graceful stop of each host shape (timers, intent, saga, projection-grain): after a plain start, and after a kill and a restart on the killed silo's endpoint; production and test profile | — (the suspected cause of the leftover entry) | **0.1–0.2 s in every case**, both shapes, both profiles | — | the harness's thirty-second patience is never reached; a slow stop is not what leaves an Active entry behind. Where the `SagaProcessTimeoutTests` entry came from stays open — its hosts' logs are not in the suite's output — and the suite is made immune instead: every test silo now runs in a cluster of its own (`stratara-poc-<port>`), so a stale entry of one class is invisible to the next, while a killed silo's successor on the same port still buries it | `raw/stop-delay/20260914-102919/` (production profile, plain), `20260914-102950/` (test profile, plain), `20260914-103033/` (test profile, both shapes) |

## Deviations from the expectations, recorded before interpretation

- **The bus control moved.** Every B3 and B5 bus number is lower than the archived one — 600
  against 851 commands/s at 2000×1 — because `dead-letter-what-a-handler-cannot-take` (#71) put the
  worker queues on quorum queues after the archived run. The ratios in this file are grain-to-bus
  within a run, as the expectations required; the absolute grain numbers are given beside them so
  the ratio is not mistaken for the whole gain.
- **R1's premise.** The archived "one to two minutes per restart" was not a restart cost. A
  same-endpoint restart takes under a second; a join on another endpoint while a killed silo's entry
  is Active fails after five minutes under any `IAmAlive` setting. The measurement was reshaped
  twice — first to the timer-firing shape, then to the join shape — and each aborted attempt is in
  `raw/restart-delay/` with an `ABORTED.md`.
- **The integration suite's one failure** is the join shape inside the test cluster, not a
  regression of the code under test; the test passes alone, and the suite is re-run with one cluster
  per test silo (below).
- **Attribution.** D1–D6 were built before the first run, so a B4 number does not separate the
  parallel resume from the batch scope, nor a B3 number the batched completion from the immutable
  envelope. The design names which decision each cost belongs to; the runs measure their sum.

## Stop criteria

None triggered: no test lost an event, and no optimisation needed a file under `src/` outside
`src/Stratara.Orleans/`. `git diff --stat main -- src` lists only that project.

## What the numbers say about the two costs that decided the recommendation

The archived recommendation — offer Orleans as an additional execution model, do not make it the
recommended one — rested on two measured costs and one operational one. After this change:

- **Processor time per command** (B5): +59 % became **+12 % with the built-in directory for
  aggregate grains, +27 % with Redis as the default directory** — 2.53 and 2.81 against 2.26 CPU-s
  per 1 000. The remainder is the intent's own durable row and the grain hop, which the durable
  shape exists to pay. Idle processor time halved and idle memory with the built-in directory is
  186 MB against the bus host's 171 MB, not 231 against 172.
- **Where the grain path was not faster** (B3, B4): a rebuild of one projection is **17.6 % of a
  full replay** instead of 94 %, with the other projections serving live events at 5 ms throughout;
  one aggregate under load is at **parity** (96–99 %) instead of 87 %, and spread over aggregates
  the grain path is 2.3–2.5 times the bus worker where it was 1.3–1.5 — half of that widening is
  the bus worker's own loss to quorum queues.
- **The hint path** (B2) is now ahead of the push at the median and at p90 in both runs; at p99 it
  is ahead in one run and behind in the other, both inside 25 ms.
- **The operational surface changed shape rather than size.** The restart delay the archive
  described does not exist; what exists is harder: a single-silo cluster cannot be joined on a new
  endpoint after a kill until a second active silo, a same-endpoint restart or a table cleanup
  buries the entry. A consumer that runs one silo behind an orchestrator that assigns new addresses
  needs to know this before the first deployment; two silos, or a stable endpoint, are the answers
  Orleans offers.

What this file does not decide is whether the recommendation changes. The costs that argued
against the default are two-thirds gone with one registration choice (the directory) and gone
entirely for the rebuild; the operational item is new and sharper. That is the owner's call, with
this file and the archived one side by side.

**Decided by the owner, 2026-09-14:** the Orleans execution model becomes the **recommended** one.
The archived recommendation of 2026-09-13 stands as the record of that day; from here on a change
that ships the execution model — packable projects, the store schema the readers need, the spec
deltas, the migration and operations notes on the documentation site — carries the recommendation
into the published surface. The condition the owner attached: the hard-death finding and its
answers are documented for consumers, which `operations-note.md` in this directory now does and the
shipping change must publish. The bus workers stay supported and undeprecated until that change
says otherwise.

## Still open after this change

- B2's p99 is not settled: one run of two inside the bound. A third run, or a longer one at the same
  rate, decides it; the median and p90 are not in question.
- Idle processor time of the silo host is 0.031 CPU-s per second against 0.020 registered; a
  profile of the idle silo would say which of Orleans' own periodic work it is.
- Which host of `SagaProcessTimeoutTests` left an Active membership entry, and why, is unexplained;
  the harness does not keep the child hosts' logs unless a test fails.
- The known limitations of the archived design stand as listed there; none was touched here.
- Three items the pre-merge review named for the change that ships the model: the completion
  queue deletes outbox rows through the write context directly, where a `DeleteManyAsync` on the
  outbox repository port would keep the Orleans package away from the table's mapping (a change
  to a shipped interface, so not here); the grains select the durable directory by name and
  nothing verifies at start-up that a host registered it; and a catch-up issues one empty read
  after its last batch, which a reader that already reads one row past the batch could spare.
