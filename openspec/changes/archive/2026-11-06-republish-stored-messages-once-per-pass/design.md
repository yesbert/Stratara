# Design

## Context

See `proposal.md` for the motivation. Both dispatchers publish through one private method that runs the
dispatch pipeline, and republication from storage calls the same method (evidence:
`EnqueueOutboxEntriesAsync` in `src/Stratara.Outbox.RabbitMQ/Outbox/CommandOutboxDispatcher.cs` and
`EventBundleOutboxDispatcher.cs`). The pipeline is three retries from 200 ms, doubling, with jitter:
about 1.4 seconds per refused message, and no timeout strategy (evidence: `AddDispatcherRetry` in
`src/Stratara.Resilience/Resilience/ResilienceFactory.cs`). The drain handles commands before bundles
under one lock (evidence: `OutboxWorker`).

## Goals / Non-Goals

**Goals:**

- A refused stored message costs one publish attempt per pass, on both dispatchers.
- A first dispatch behaves exactly as before.

**Non-Goals:**

- A time budget for a pass, and stopping a topic after a failure. The owner decided against both on
  2026-11-06.
- Renewing the lease during a pass.
- Any change to `BatchSize`, `LockLeaseSeconds` or their defaults.

## Decisions

### Republication bypasses the pipeline; the first dispatch does not

The private publish method takes the pipeline to run under. The first dispatch passes the named one,
republication passes `ResiliencePipeline.Empty`, and the catch clauses and the failure logging stay
shared. Evidence: the tests of task 1.1 and 2.1 count publish attempts under a real retry pipeline.

Rejected: a shorter, separately named pipeline for republication. One attempt has no policy left to
name, and a new name is public surface nobody would configure.

Rejected: calling the bus directly in a second method. It would duplicate the failure handling, which
is exactly what differs between the two dispatchers and must not drift within one.

### No retry in the pass, because the interval is the retry

The stored message is already durable, so a retry inside the pass buys only an earlier publication
of that one message, paid for by every message behind it. The next pass comes `PollingIntervalSeconds`
later and retries everything. Evidence: the requirement that a pass does not attempt a refused message
again, which the retries were already at odds with in substance.

### One attempt for every message, whatever failed before it

A refusal is often specific to a topic: a heavy command with no heavy worker says nothing about the
ordinary command behind it. Owner's decision of 2026-11-06.

## Risks / Trade-offs

- [A broker that is unreachable rather than refusing may spend a connection timeout on each attempt, so
  a large batch can still outlive the lease] → Unchanged in kind and smaller in size than before. The
  owner chose no time budget; at-least-once delivery absorbs the overlap, as it does today.
- [A publication that fails only for a moment now waits for the next interval] → It was stored already,
  and handlers tolerate later delivery. The wait is at most one `PollingIntervalSeconds`.
- [A host that replaced a dispatch policy to shape republication loses that effect] → Stated in the
  proposal, and the `resilience` delta states which path the policies govern.

## Migration Plan

None. Deploy; rolling back restores the retries. Rows in storage are not touched.
