# keep-a-stop-and-a-resume-honest

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

The review that replaced Copilot's on the pull requests merged on 2026-09-17 followed a recorded
command through a resume and a stop, and found three places where the model promises more than it
keeps.

- **The resume routes by columns nothing verifies.** The record's signature covers the envelope's id,
  its type, its session, its heavy claim and a digest of the payload. The resume then routes by the
  row's own `Heavy` and aggregate columns. Somebody who can write to the store can flip the heavy
  column of a signed record and have it run outside the heavy pool's bound, or outside its aggregate's
  order — with the signature still verifying, because the signature never covered the column.
- **A stopping aggregate holds its callers until it is collected.** The queue is given up in
  `OnDeactivateAsync`, and the runtime deactivates an activation only once the requests waiting for it
  have ended. The requests waiting for it *are* the queued commands, so their callers wait for the
  runtime to give up on the deactivation instead of being told the silo stopped, and the silo's stop
  waits with them.
- **A timer's tick can delete the reminder that renewed it.** The check for a renewal and the
  unregister after a handler run outside the grain's own gate, so a registration for the same purpose
  and due time from another turn — a saga re-applying a fact — can land between them and have its
  reminder deleted by the tick it renewed.

A fourth, smaller: two claimers that stamp a batch in the same millisecond read each other's rows back
as their own, so a command is handed over twice. It runs once all the same, because the grain that
receives it refuses a hand-over it already holds, and only the attempt is counted twice — which is why
this change writes it down rather than changing the stamp, whose millisecond truncation is what lets
every provider compare it.

## What Changes

- **A resumed command runs where its signed envelope says.** The row's identity and heavy claim are
  held to the envelope; a record whose row disagrees is kept under strict mode and resumed by the
  signed claim under permissive mode, as a record that does not verify is. The aggregate a row names
  stays unsigned — it decides which activation accepts the command, not what it writes, and the
  store's version check answers for that — and the documentation says so.
- **A stopping aggregate gives up its queue at once.** The loop abandons what is still queued when it
  ends on the stop, and a command accepted afterwards is given up as it arrives.
- **The tick's unregister runs under the gate**, so a renewal cannot be deleted by the tick it renewed.
- **What the claim's stamp can and cannot tell apart is written down** where the claim is implemented.
- **A deactivation runs to its end** whatever the wait for the running handler ended with.

## Impact

- Affected specs: `orleans-execution`
- Affected code: `src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs`,
  `src/Stratara.Orleans/Aggregates/AggregateGrain.cs`,
  `src/Stratara.Orleans/Timers/TimerOwnerGrain.cs`,
  `src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs`
- Affected docs: `docs/guides/hmac-bus-envelope.md` (what the record's signature covers),
  `docs/reference/log-events-schema.md`, `CHANGELOG.md`
- No public API changes.
