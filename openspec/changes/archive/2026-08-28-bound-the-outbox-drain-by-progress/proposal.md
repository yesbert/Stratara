> **Status:** approved

# The outbox drain stops when it stops making progress

## Why

The worker that drains durable storage reads a batch, hands it to the dispatcher, counts it as
published, and reads again — looping until a batch comes back empty. An entry is removed only once
it has actually been delivered.

**Nothing checks that anything was delivered.** When a batch is handed over and none of it goes out,
nothing is removed, so the next read returns *the same batch*. The loop's condition — a batch came
back non-empty — is satisfied by the batch it just failed to deliver. It reads, fails, counts, and
reads the same rows again, with no delay between passes, until the host shuts down.

Two ordinary conditions reach it, and only one of them involves a replay:

- **The broker is unreachable.** Every publish attempt fails and is swallowed as a logged failure,
  so nothing is removed. This needs no replay and no unusual configuration — a broker outage alone
  is enough.
- **A projection replay is in progress.** Draining is deliberately suppressed, so the dispatcher
  returns without touching anything.

Both turn the drain into a hot loop against the database. And because the command drain never
returns, the event drain that follows it never starts — a broker outage stops the event outbox by
starving it, not by failing it.

The counter compounds it. `OutboxEntriesPublished` is incremented by the size of the batch that was
*read*, not by what was delivered, so the one signal that would show an operator the outbox is stuck
instead climbs at full speed. A dashboard shows records being published at an extraordinary rate at
exactly the moment nothing is being published at all.

The requirement covering this says the worker loops "until a batch comes back empty". That is the
defect, written down as if it were the design — which is why it survived.

## What Changes

- **A drain pass handles one batch and ends.** Undelivered entries stay stored and are retried on
  the next interval, which is what the interval is for. A pass can no longer repeat work it has
  already failed at, because it does not repeat at all — the spin is removed structurally rather
  than detected.
- **Publication is counted where publication happens.** The count moves from the worker, which knows
  only what it read, to the dispatchers, which know what actually went out. An entry that could not
  be delivered is no longer counted as published.
- The requirement is amended: a drain pass is bounded by the work it can actually do, not by reading
  until the store comes back empty.

Not breaking: no signature changes, no configuration changes, nothing a consumer calls behaves
differently. Two observable shifts a consumer should know about:

- **`OutboxEntriesPublished` reports lower numbers, and correct ones.** Any alert threshold derived
  from its previous behaviour was calibrated against inflated values. The name and its tags are
  unchanged.
- **A large stored backlog now drains at one batch per interval** rather than in a single pass. With
  the default batch size and interval that is twenty thousand entries a minute, and both are
  configurable. Durable storage is the fallback path — the one taken when the bus is down or a
  replay is running — not the steady-state throughput path.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `outbox-and-messaging`: the requirement covering how the worker drains durable storage currently
  specifies looping until a batch comes back empty. It is amended so that a pass is bounded, and
  gains a scenario for a batch that cannot be delivered.

## Impact

- `Stratara.Outbox.RabbitMQ` — the drain worker and both dispatchers.
- `Stratara.Diagnostics` — unchanged. The counter keeps its name and its tags; only where it is
  incremented, and what it counts, changes.
- Consumers with alerting on the published counter: thresholds tuned against the inflated values
  will need revisiting. This is the only consumer-facing action.
- Source: a consumer's framework-findings report, which recorded this as a defect in its own right
  alongside the stuck-replay-marking finding and split it out deliberately, because a broker outage
  triggers this with no replay involved. No legacy source is dissolved or superseded by this
  change.
