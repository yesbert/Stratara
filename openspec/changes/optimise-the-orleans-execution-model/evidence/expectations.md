# Expectations — registered before the first run

> **Confirmed by the owner:** 2026-09-14, as written. No file under `evidence/raw/` existed before this line
> carried a date.

Every measurement below states its archived baseline, what it expects after the optimisation, and
what would falsify it. A number written after a run is not a threshold; if a run shows that a
threshold was badly chosen, the run stands as recorded and the threshold is changed in a later
commit that says why.

## Where the runs happen

| | |
|---|---|
| Machine | Apple M2 Ultra, 24 cores, 64 GB |
| Container runtime | Docker 29.7.2, 24 CPU, 47 GB |
| .NET SDK | 10.0.401 |
| Orleans | 10.3.1 for every Orleans package |
| Images | `postgres:17-alpine`, `redis:7-alpine`, `rabbitmq:4-management-alpine` |
| Silo profile | `Production` (default reminder and drain settings) unless a row says otherwise |
| Commit | recorded per run |

Bus workers and silo run on this machine in the same session, against containers started by the
run. Nothing runs in the cloud and nothing costs money. No single run is expected to exceed 30
minutes; if one would, it is asked for first.

## What a raw result is

`evidence/raw/<measurement>/<yyyyMMdd-HHmmss>/` with `environment.json` (commit, machine, images,
settings) and `result.json` (every individual observation, never only aggregates). A run that was
aborted is committed like any other, with the reason in `console.log`.

## The control

Every run carries the bus path. The comparison that counts is **grain-to-bus within the run**,
against the **archived grain-to-bus ratio** — not the archived absolute number, which was measured
on another day. Where an absolute bound is stated it is stated in addition.

## Performance — grain path against the bus path, same run

| # | Measurement | Archived (2026-09-13) | Expectation | Falsified if | Duration |
|---|---|---|---|---|---|
| B4 | Rebuild of one projection against the full replay, 100 000 events over 3 projections, live events at 20/s | 94 % of the replay's duration; live events reached the untouched projection at p50 3 ms, p99 17 ms | ≤ 40 % of the replay's duration after D1 and D2; live p99 ≤ 50 ms | > 60 % after D1 and D2 and still > 60 % after the optional index (task 2.4); or live p99 > 50 ms; or 0 live events applied by the others | 15 min |
| B2 | Event to read model, 3 × 2 000 events at 50/s, hint path against bus push | hint p50 3.2 ms against push 2.9–3.0; hint p99 12.3–16.6 against push 9.0–10.8 | hint p50 ≤ push p50 + 0.2 ms; hint p99 ≤ 10 ms and ≤ push p99 + 1 ms | hint p99 > push p99 + 3 ms, or any event not applied | 3 min |
| B3 | Commands per aggregate, 3 × 2 000 commands, medians | intent 131 / 150 / 87 % of bus at 2000×1 / 20×100 / 1×2000; synchronous 166 / 223 / 99 %; 0 conflicts | intent ≥ 130 / 140 / 95 %; synchronous unchanged within ±10 points; 0 conflicts on both directory settings | intent < 120 % at 2000×1 or 20×100, or < 90 % at 1×2000; or one conflict under the Redis-default setting | 5 min per directory setting |
| B5 | Resource use, 60 s idle then 60 s at 200 commands/s, silo host against bus host | 4.04 against 2.54 CPU-s per 1 000 commands (+59 %); 231 against 172 MB idle | ≤ 3.0 CPU-s per 1 000 (≤ +20 % of the bus host in the same run); idle processor time ≤ 0.02 CPU-s per second; idle RSS not above the archived 231 MB | > +30 % of the bus host in the same run, or fewer than 12 000 of 12 000 applied | 5 min per directory setting |

## Restart delay

| # | Measurement | Archived | Expectation | Falsified if | Duration |
|---|---|---|---|---|---|
| R1 | Restart on the same endpoint to first successful grain call, 3 restarts per setting, single silo | one to two minutes per restart (T5: 23 minutes for 10 kills) | default settings: 60–120 s, recorded; shortened profile: ≤ 30 s at every restart | shortened profile > 45 s at any restart | 10 min |
| R2 | `HardKillTimerTests` under the shortened profile, 10 kills | 10 of 10 as expected under the defaults | 10 of 10 as expected; no silo declared dead while alive in the log | one false death vote, or one wrong firing | 10 min |

## Correctness — must stay as it was

Every integration test of the archived change runs on the final code and must pass with what it
asserted before: T1 (0 of 20 lost on the checkpoint path), T2, T3 (500 of 500 in order), T4, T5,
T6, plus `DurableIntentTests`, `SagaProcessTimeoutTests`, `SagaGrainTests`, `ProjectionGrainTests`,
`SingletonWorkTests`, `ResetTests`, `CoHostingTests`. A test whose assertion had to change to pass
is a falsified expectation and is recorded as such in `results.md`, with the reason.

## Stop criteria

Work stops and is reported when the checkpoint path loses an event in any test, or when an
optimisation cannot be done without changing a file under `src/` outside `src/Stratara.Orleans/`.
