# Results — against the expectations of 2026-09-13

> Runs on 2026-09-14 on the machine of
> `../../archive/2026-09-13-prove-an-orleans-execution-model/evidence/environment.md`; raw output
> under `raw/`, one directory per measurement and run, written by the harness before anybody read
> it. The thresholds are the ones pre-registered in `expectations.md`.

| Id | Expectation | Result | Verdict | Raw |
|---|---|---|---|---|
| K1 | 0 of 20 events lost with `Outbox:DurableBundles = true`, the host ended between commit and publish each time | **0 of 20 lost**; every view appeared after the restart, delivered by the restarted host's outbox worker (polling every second) | **holds** | `raw/commit-publish-kill-durable/20260914-062647/result.json` (the runner reported 45 s for the 20 kills; the raw file records counts, not time) |
| C1 | durable ≥ 85 % of bus-first appends/s at 8 and at 32 writers, spread, medians of 3 × 10 000, bus accepting instantly | bus-first 604 / 2 646 / 2 650 appends/s at 1 / 8 / 32 writers; durable 435 / 1 896 / 1 861 — **72.0 % / 71.7 % / 70.2 %** | **falsified** at both bounded settings, by 13–15 points | `raw/durable-bundles/20260914-062703/result.json` |

## What the numbers say

**K1 closes the finding for the bus path.** The same harness that lost 20 of 20 bundles on the
default path in the proof of concept (T1 there) loses none with the option on. The window is
closed by construction, and the outbox worker's polling interval is the only latency a crash adds.

**C1 is more expensive than estimated.** Design D1 expected the insert to cost "well under" the
44 % the proof of concept's contended counter update cost, and set 15 % as the bound. The measured
cost is 28–30 % at every writer count — 28.0 % at one, 28.3 % at 8, 29.8 % at 32. Three things are
in that number, none of them a contended lock:

- the insert of the serialised bundle in the commit — the row carries the events again, so the
  transaction writes roughly twice the bytes;
- the delete after acceptance — a second round trip on the caller's path (design D3), which the
  one-writer number shows most clearly;
- an instant bus, which makes the comparison an upper bound on the *relative* cost: a real broker
  adds the same latency to both layouts and shrinks the ratio.

The estimate was wrong; the option is not. What it buys — a committed fact that cannot be lost for
any subscription — is what the specification now promises for it, and 70 % of 2 600 appends/s is
still three and a half to four times the proof of concept's portable-counter layout on one bucket
(548 and 489 appends/s at 8 and 32 writers in B1 there).

## What follows from it

- **The default stays `false` in 4.x**, as decided in D1 for reasons of semantics, and the number
  now also answers the 5.0 question the design left open: a 30 % throughput cost is not a default.
  A host that runs sagas on the bus path, or cannot afford a replay, switches it on knowingly.
- **The guide states the cost as measured** (`docs/guides/outbox-setup-rabbitmq.md`): about 30 %
  fewer appends per second at 8–32 writers against an instant bus, less against a real one.
- **A cheaper shape is possible and is not this change:** deferring the delete to the drain
  (rejected in D3 for the table's steady state) would remove the second round trip; measuring
  that trade is a follow-up if a consumer asks for it.
