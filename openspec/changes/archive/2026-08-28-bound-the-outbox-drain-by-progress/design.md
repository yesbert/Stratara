# Design — The outbox drain stops when it stops making progress

## Context

See `proposal.md` — *Why*.

The shape today, in both `HandleUnpublishedCommandsAsync` and `HandleUnpublishedEventsAsync`:

```
read a batch
while the batch is not empty:
    hand it to the dispatcher
    add the batch's size to the published counter
    read another batch
```

The dispatcher returns `Task`. It deletes an entry when the bus accepts it and leaves it otherwise,
and it returns early without touching anything while a replay is active. Nothing about what it did
reaches the caller, so the worker's only evidence of progress is that a read returned rows — which
it will, forever, for rows nobody could deliver.

Evidence: the source as it stands at `e4a76f41`, and the consumer report that surfaced it, where it
is recorded as a defect in its own right alongside the stuck-replay-marking finding.

## Goals / Non-Goals

**Goals:**

- A drain pass cannot repeat work it has already failed at.
- The published counter counts publications.
- Undelivered entries are still retried, on the next interval.

**Non-Goals:**

- Back-off, dead-lettering or a poison-message limit for entries that keep failing. Today they are
  retried every interval forever; that stays true and is a separate question.
- The replay-suppression check inside the dispatchers. It is correct — draining during a replay is
  meant to be suppressed. What was wrong is that the worker could not tell suppression from progress.
- Changing the polling interval or batch-size defaults.

## Decisions

### One batch per drain pass, instead of looping until storage is empty

Removing the inner loop makes the spin impossible rather than detectable. There is no condition to
get right, no state to carry between reads, and no way for a future edit to reintroduce it: a pass
reads once, dispatches once, and ends. What used to be the loop is now the polling interval, which
already exists and already has the semantics we want — come back later and try again.

Undelivered entries are retried on the next pass, which is what durable storage is for. The entries
that *were* delivered are gone, so the next pass sees a strictly smaller store; progress across
passes is preserved even though no single pass drains everything.

*Alternative rejected — the dispatcher returns how many entries it delivered, and the loop runs while
that is greater than zero.* Semantically the cleanest of the three: the loop would then be bounded by
the one fact it actually needs, and the counter would fall out of the same value. It is rejected for
a reason outside the code — it changes `EnqueueOutboxEntriesAsync` on two published interfaces from
`Task` to `Task<int>`, which is a signature change on the published surface and therefore a major
version. The owner has decided to collect the current run of consumer-reported fixes into one
non-breaking release, and this defect does not justify overriding that on its own. **If a major is
opened for other reasons, this is the shape to adopt** — it is better than what is being built here,
and this note is where a future reader should find that out.

*Alternative rejected — keep the loop and detect a lack of progress by comparing entry identifiers
across reads.* Non-breaking, and it keeps single-pass drain throughput. It re-derives "did anything
go out" from a side channel, needs a set of identifiers carried across iterations, and still needs
the counter moved to be correct. More moving parts guarding an invariant that removing the loop
gives for free.

### The counter moves into the dispatchers

The worker knows what it read; only the dispatcher knows what the bus accepted. Incrementing there,
once per accepted entry, is the smallest change that makes the number mean what its name says.

It also survives the decision above. Had the counter stayed in the worker with one batch per pass,
it would still have counted an undelivered batch as published once per interval — quieter than
today, and just as wrong.

The counter keeps its name and its `OutboxKind` tag, so no dashboard or alert breaks structurally.
Its *values* drop to the truth, which is a real change for anyone who tuned a threshold against the
inflated ones, and the proposal says so.

### The two kinds keep draining independently

Today a command drain that never returns starves the event drain that follows it. With the loop gone
this cannot happen, but the spec now states it as a scenario rather than leaving it as an accident of
control flow, so a future change that reintroduces a loop in one of them has something to fail
against.

## Risks / Trade-offs

- **A large stored backlog drains more slowly.** → One batch per interval instead of all of it in one
  pass. With the defaults that is 10 000 entries every 30 seconds; both are configurable, and durable
  storage is the fallback path rather than the steady-state one. A consumer who has accumulated a
  backlog large enough to care can shorten the interval or raise the batch size, neither of which was
  available as a remedy for an infinite loop.
- **An alert calibrated on the inflated counter goes quiet.** → Named in the release notes as the one
  consumer-facing action. The failure direction is benign: the number gets smaller and correct, so an
  alert on "too high" stops firing spuriously rather than starting to miss real events.
- **Entries that can never be published are retried every interval forever.** → Unchanged by this
  change, and explicitly out of scope. Worth its own change; noting it here so it is not mistaken for
  something this one addressed.

## Migration Plan

No data migration and no configuration change. Adoption is a version bump. Rollback is the previous
package version; nothing written under this change is unreadable by it.
