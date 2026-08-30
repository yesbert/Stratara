> **Status:** approved

# A replay marking that nobody is renewing expires

## Why

While a projection replay is active, the framework suppresses publication — that is deliberate, so
that replayed history does not re-trigger side effects. The marking that says "a replay is active"
is written once and cleared when the replay finishes.

**It is only ever cleared by the process that set it.** The replay clears it on its way out,
including when it fails. A process that stops without an on-the-way-out — a kill, a container stop,
an out-of-memory kill, a reboot — never clears it, and nothing clears it on the way back up. The
marking outlives the replay that justified it, with no end.

**What that costs is not "replays are blocked".** Publication suppression is global. While the
marking stands:

- every command is recorded instead of sent, and the caller gets an identifier and a success
  response for a command that will never run;
- the outbox never drains, because draining is suppressed by the same marking;
- anything a consumer suppresses on the same signal stays suppressed.

The write path keeps answering *yes* while doing nothing. Nothing surfaces it: the marking looks
exactly like a replay that is genuinely running, and the progress figures frozen beside it look like
a replay that is merely slow.

Observed on a consumer's test environment on 2026-07-29: the marking stood with a processed count of
188 000 against a total of 280 261, none of the three values set to expire, while the worker had been
idle since its restart. Clearing them by hand let the next replay run to completion and clean up
after itself — the ordinary path was never broken, only the crash path.

## What Changes

- The marking that suppresses publication during a replay, and the progress figures beside it, are
  held on a **lease that the replaying process renews as it works**. A replay that stops without
  clearing them stops renewing them, and they lapse on their own. Suppression then ends, the outbox
  drains, and commands are sent again — without an operator having to know that a key exists.
- The lease length is **configurable, with a default long enough for a slow batch**. This is the
  parameter the design has to get right: a lease shorter than the slowest stretch of work between
  two renewals would lapse while the replay is still running, and side effects would fire against
  live projections mid-rebuild. The default is chosen against that failure, not against how quickly
  an abandoned marking clears.
- No new operation. Marking a replay finished is already on the published interface and is the
  operator's manual reset; what was missing is that an abandoned marking clears itself.

Not breaking. A replay that runs and finishes behaves exactly as before, and a consumer that never
crashes a replay sees no difference.

Out of scope, deliberately:

- The outbox drain loop that re-reads an undelivered batch and counts it as published each time.
  It is a defect in its own right — a broker outage alone triggers it, with no replay involved — and
  it is being handled as its own change against the outbox capability.
- Whether every worker replica subscribed to the replay-request signal should start its own replay.
  Found while reading this code; unrelated to the lease, and not investigated here.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `projections`: the requirement covering how a replay marks itself active and inactive gains the
  guarantee that the marking is held on a renewed lease, so that a replay whose process stops
  without clearing it does not suppress publication indefinitely.

## Impact

- `Stratara.Outbox.RabbitMQ` — the replay-state implementation that holds the marking and the
  progress figures, and its registration. This is the only package that changes: the replay worker
  already reports progress once per batch, so renewal is folded into what reporting progress means
  rather than added as a second call the worker has to make.
- `Stratara.Projections` — unchanged. Named here because it is the package a reader would expect to
  carry the renewal, and it does not.
- Consumers currently carrying their own workaround for a stuck marking (a reset at worker start, an
  operator action) can keep it; it becomes a second line rather than the only one. Nothing they call
  changes shape.
- Source: a consumer's framework-findings report. No legacy source is dissolved or superseded by
  this change.
