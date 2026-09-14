# Expectations — written before the first run

> **Status:** pre-registered 2026-09-13, before either measurement ran. The thresholds are not
> changed after a run; a result that misses one is recorded as a falsification.

Both measurements reuse the harness of `prove-an-orleans-execution-model` on the same machine
(`../../archive/2026-09-13-prove-an-orleans-execution-model/evidence/environment.md`), so the
numbers are comparable to that change's T1 and B1.

| Id | Measurement | Expectation | Falsified when |
|---|---|---|---|
| K1 | `CommitPublishKillTests` → durable bundle path: the host is ended between the commit and the publish, 20 times, with `Outbox:DurableBundles = true` and the outbox worker polling every second | 0 of 20 events lost — the restarted host's drain delivers the stored bundle and the view appears within 15 s | any event lost |
| C1 | `--durable-bundles` run: appends per second through the real event source and the real bundle dispatcher with a bus that accepts instantly, `DurableBundles` off (bus-first) against on, streams spread over the buckets, 1 / 8 / 32 writers, medians of 3 × 10 000 appends | durable ≥ 85 % of bus-first at 8 and at 32 writers — the insert joins a transaction that already exists, the delete is one more round trip on the caller's path | below 85 % at 8 or at 32 writers; the 1-writer number is reported, not bounded, because a single writer is latency-bound and the extra round trip shows there first |

What the numbers decide: K1 decides whether the option does what the specification says. C1
decides how the guide describes the cost and informs the default question for 5.0 (design D1); it
does not decide whether the option ships.
