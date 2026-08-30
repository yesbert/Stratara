# Design — A replay marking that nobody is renewing expires

## Context

See `proposal.md` — *Why*.

The shape today. `ProjectionReplayState` (Redis-backed, singleton, registered from the outbox
package) holds four values under `stratara:projection:replay:*` — `active`, `processed`, `total`,
`error`. `Activate()` writes `active` and deletes `error`; `SetProgress()` writes `processed` and
`total`; `Deactivate()` deletes all four; `SetFailed()` deletes `active` and writes `error`. None of
the writes carries an expiry.

`ProjectionReplayWorker.RunReplayAsync` calls `Activate()`, then truncates, then loops over batches,
calling `SetProgress(totalReplayed, totalEvents)` once per batch, with `Deactivate()` in a `finally`.
That `finally` is the only thing that ends suppression, and a killed process does not run it.

Both dispatchers read `IsReplayActive` before publishing and return early when it is set, which is
how a standing marking reaches the write path.

Evidence: the source as it stands at `d88ff9d2`, plus a consumer's field observation of 2026-07-29
recorded in its framework-findings report.

## Goals / Non-Goals

**Goals:**

- An abandoned marking clears itself, with no operator action and no knowledge of the storage.
- A marking belonging to a replay that is genuinely running never clears itself.
- The progress figures do not outlive the marking they describe.

**Non-Goals:**

- A new operation for an operator reset. Marking a replay inactive is already published and already
  does exactly that.
- The outbox drain loop. Separate change, separate capability — a broker outage triggers it with no
  replay involved, so folding it in here would file it under the wrong cause.
- Whether several worker replicas should each start a replay on the same request. Noticed while
  reading; not investigated, not changed.
- Detecting *which* host owns a marking. A lease makes ownership irrelevant, which is the point.

## Decisions

### A renewed lease, not a reset when the worker starts

Both were on the table and the consumer that reported this is building the reset-at-start version on
its own side. For the framework it is the wrong mechanism, for a reason that only shows up with more
than one replica: a replay is requested by a broadcast that **every** subscribed worker receives, so
more than one host can be replaying. A host that clears a standing marking as it starts would clear
one belonging to a replay running on another host — turning crash recovery into a way to resume
publication in the middle of a live rebuild. That is the exact failure the marking exists to prevent,
reached by the mechanism meant to protect it.

A lease has no such coupling. It is not "who set this" but "is anyone still working"; only a process
actually doing the work renews it, and a restarting host neither renews nor clears anything.

A consumer that already resets at start keeps a correct second line — a reset is still a deliberate
statement that no replay is running here.

### Renewal rides on the progress report, once per batch — inside the state, not the worker

The worker already calls `SetProgress` once per batch, from inside the loop, in proportion to work
actually done. Refreshing the lease **inside that method** rather than at a new call site means the
replay worker does not change at all: it already reports progress at exactly the rhythm the lease
needs, and it does so for its own reasons. Renewal becomes a property of what it means to report
progress — "I am still working" — instead of a second thing every future caller has to remember.

It also needs no timer and no concurrent task, which matters because framework code may not start
threads or background tasks; a heartbeat independent of the batch rhythm would mean a second loop
with its own cancellation and failure handling for a marginal gain.

The price is the constraint below: the lease must outlast the slowest single batch, because between
two renewals nothing is happening on the key.

*Alternative rejected — renew on every `IsReplayActive` read.* Cheap and frequent, and wrong: the
readers are the dispatchers, so suppression would keep itself alive. An abandoned marking would be
renewed by the very traffic it is suppressing, and the lease would never lapse.

### The default lease is long, deliberately

The two failure directions are not symmetric.

- **Too short** — the lease lapses while a replay is running. Suppression stops mid-rebuild, side
  effects fire against half-built read models, and the replay finishes without anyone learning that
  it happened. Silent and unbounded.
- **Too long** — an abandoned marking suppresses publication for the remainder of the lease. Bad,
  bounded, and self-healing; it is also strictly better than today, where the bound is *never*.

So the default errs long. Reference points from the same repository: the outbox lock lease defaults
to 60 seconds for a batch of up to 10 000 entries, and a replay batch defaults to 5 000 entries but
does materially more per entry — decrypt, map, then run every projection handler. A default of five
minutes sits several times above that and still bounds an abandoned marking to minutes rather than
forever.

It is configurable because the right value depends on the consumer's slowest batch, which the
framework cannot know. The option carries that in its documentation, stated as the constraint it is:
longer than your slowest stretch between two progress reports.

### The progress figures share the lease; the recorded failure does not

`processed` and `total` describe the marking. If they outlived it, a reader would see no active
replay beside a frozen count and read it as "a replay finished part way" — which is what the field
observation looked like. They lapse together.

`error` is different: it is a message to an operator about a replay that already ended, it is
deleted by the next `Activate()`, and nothing branches on it. It keeps no expiry.

## Risks / Trade-offs

- **A consumer's slowest batch exceeds the default lease.** → Then the marking lapses mid-replay and
  side effects fire, which is worse than today for that consumer. Mitigated by a generous default,
  by the option's documentation naming the constraint, and by the batch size being the consumer's own
  setting — a host tuned to very large batches is a host that has already thought about batch
  duration. This is the risk worth reviewing hardest.
- **Redis without key expiry configured, or a clock skew between replicas.** → Expiry is server-side
  and relative, so skew between application hosts does not enter. A deployment that disables key
  eviction still honours per-key TTLs; the two are different mechanisms.
- **Truncation runs between `Activate` and the first progress report.** → That stretch carries no
  renewal, so a read store whose truncation outlasts the lease would lapse before the first batch.
  Truncation is a single transaction and normally far quicker than a batch, and the default leaves
  minutes of headroom, but it is the one gap the per-batch rhythm does not cover and it is why the
  default is not tuned to batch duration alone.
- **The lease is renewed one batch too late on a very long final batch.** → Suppression ends slightly
  before the replay does. Same mitigation as the first risk; the default is chosen so the margin is
  large.

## Migration Plan

No data migration and no configuration required. A consumer adopting this version gets the default
lease; keys already standing from before adoption were written without an expiry and keep none —
they must still be cleared once, by the operator or by the next replay's `Deactivate()`. That
one-time step is worth calling out in the release notes, because a consumer sitting on a stuck
marking today will not be freed by upgrading alone.

Rollback is the previous package version; nothing written under this change is unreadable by it.
