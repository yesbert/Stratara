## Context

See `proposal.md` — Why. The current code:

- **Recording.** `IntentRecorder.RecordAsync` (`src/Stratara.Orleans/Aggregates/IntentRecorder.cs:22-37`)
  builds a `CommandEnvelope` from the serialised command, its qualified type name and
  `JsonSerializer.Serialize(session)`, with `Signature` left at its default of `null`, and hands it to
  `ICommandIntentStore.RecordAsync`; `CommandIntentStore.RecordAsync`
  (`src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs:23-38`) serialises the whole
  envelope into `OutboxEntry.DataJson`. The envelope type already has the field
  (`src/Stratara.Contracts/Messages/CommandEnvelope.cs:31`), pinned by name on the wire.
- **The bus side.** `CommandOutboxDispatcher` takes an optional `IBusEnvelopeSigner`
  (`src/Stratara.Outbox.RabbitMQ/Outbox/CommandOutboxDispatcher.cs:47`) and signs
  `BusEnvelopeCanonical.Of(envelope)` whenever one is registered (`:57-60`), regardless of mode.
  `MediatorCommandWorker.VerifyEnvelopeIntegrity` (`src/Stratara.Outbox.RabbitMQ/Mediator/MediatorCommandWorker.cs:170-203`)
  calls `BusEnvelopeIntegrityVerifier.Verify(signer, mode, canonical, signature, out failure)` and logs
  four distinct events — 105_004/105_003 (unsigned/invalid, permissive) and 105_105/105_104
  (unsigned/invalid, strict) — throwing under strict. The canonical form
  (`src/Stratara.Abstractions/Messaging/BusEnvelopeCanonical.cs:45-56`) covers id, type name, session
  JSON, the heavy flag and a digest of the command JSON — every field the record carries.
- **Resuming.** `IntentResumer.ResumeDueAsync` (`src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs:147-185`)
  reads one batch with `GetDueAsync`, keeps the exhausted ones, and for each other row calls
  `TryClaimAsync(id, expectedLastHandedOverAt, now)` — one guarded `ExecuteUpdateAsync` per row
  (`CommandIntentStore.cs:65-79`) — then hands over. `IntentResumer.Create` (`:137-145`) builds a resumer
  for a drain-only silo from the services at hand. The live hand-over passes the in-memory payload to
  the grain (`:47-49`); only the resume reads storage back.
- **The drain.** `OutboxDrainWork.RunAsync` (`src/Stratara.Orleans/Singleton/OutboxDrainWork.cs:30-50`)
  runs `ResumeDueAsync(BatchSize)` once per run; the grain timer fires every `PollingInterval`
  (`:27`, default 5 s, `:134`; `BatchSize` 100, `:137`). The bus outbox worker is the same shape
  (`src/Stratara.Outbox.RabbitMQ/Outbox/OutboxWorker.cs:58-72`), and the `outbox-and-messaging` spec
  requires a pass to be bounded by the work it can complete and never to depend on storage coming back
  empty.
- **The port.** `ICommandIntentStore` (`src/Stratara.Abstractions/Abstractions/Outbox/ICommandIntentStore.cs`)
  is public; `ICommittedPositionReader.HeadAsync` (#109) is the precedent for adding a member with a
  default implementation.

## Goals / Non-Goals

**Goals:**
- A host that signs its bus envelopes gets the same guarantee for the recorded command: what is
  resumed is what was dispatched, under the session it was dispatched under.
- The two failures are as distinguishable, and the rollout as documented, as on the bus.
- A backlog drains at the handlers' pace, and a pass's cost at the store is bounded.

**Non-Goals:**
- Signing where the host has no signer, or a signer of the intent path's own. The bus signer and its
  mode are the host's one integrity decision.
- Verifying the live hand-over: the payload the grain receives never left the process.
- Draining bundles until empty. The bundle pass keeps the bus outbox's bounded shape; this change is
  about the recorded commands.
- Encrypting the session at rest. It is the same session the bus carries in clear.

## Decisions

### D1 — The record is signed with the bus signer over the bus canonical form

`IntentRecorder` takes an optional `IBusEnvelopeSigner` and, when one is registered, records the
envelope `with { Signature = signer.Sign(BusEnvelopeCanonical.Of(envelope)) }` — the bus dispatcher's
line, on the same type, before `RecordAsync`. Nothing else changes: the store serialises the envelope
whole, so the signature lands in the row with no schema change, and a row written by a host without a
signer, or by 4.1.1, carries `null`.

Why the same signer and canonical form rather than an intent signer: the threat is the same field
(the session context) read back from a place the host may not fully trust, and one key, one mode and
one rollout are what an operator can run. The canonical form already covers the id — so a record
cannot be moved under another id — and the heavy flag.

*Rejected: a signer registered only for the intent path.* A second key to distribute, a second mode
to roll, for the same claim.
*Rejected: an HMAC over the row rather than the envelope.* The row's columns are the store's shape;
the envelope's canonical form is the one both sides already agree on and the one the bus consumer
verifies after the same JSON round trip.

Evidence: `IntentRecorder.cs:25-32`; `CommandOutboxDispatcher.cs:47,57-60`; `BusEnvelopeCanonical.cs:45-56`;
`CommandIntentStore.cs:31`. Test: the recorder with a fake signer records the signature the signer
returned for the canonical form of the envelope it recorded; without a signer it records `null`.

### D2 — The resume verifies under the host's mode, keeps at once under strict, logs four distinct events

`IntentResumer` takes the optional signer and `IOptions<BusEnvelopeIntegrityOptions>` (default `Off`
where none is configured, as on a drain-only silo through `Create`). Before claiming, each due record
is verified with `BusEnvelopeIntegrityVerifier.Verify(signer, mode, BusEnvelopeCanonical.Of(intent.Envelope), intent.Envelope.Signature, out failure)`:

- `Skipped` or `Verified` — as today.
- `RejectedStrict` — `KeepAsync` at once with `RecordFailureAsync` stating the reason (*carries no
  signature* / *signature does not verify*), no claim, no attempt; the metric `orleans.intent.kept`
  counts it; the command after it is still resumed. A retry could not change the answer, and an
  operator who returns the command after fixing the key (`kept_at = NULL, attempt_count = 0`) gets it
  verified again on the next pass.
- `RejectedPermissive` — logged and resumed as today.

Four events in `LogEvents.Orleans`, the next free ids in the band (117_114–117_117 as of #109;
renumber at implementation if another change has taken any): *IntentUnsignedResumed* and
*IntentIntegrityResumed* (warning, permissive), *IntentUnsignedKept* and *IntentIntegrityKept*
(error, strict), each with the intent id — the shape of 105_004/105_003/105_105/105_104, because the
`bus-envelope-integrity` spec requires the unsigned and the invalid case to be distinguishable by the
record's identity and an operator to be able to alert on one without the other.

Why keep rather than refuse with an exception: the resume is a loop over rows, not a message a broker
can nack; the outcome an operator can act on is the kept row with its reason, which the *Kept
commands* procedure already covers.

*Rejected: verifying in the grain before the handler.* The grain receives an `AggregateCommandEnvelope`
without a signature, built from the record by the resumer; the resumer is where the record is read
and where the keep is one call away.

Evidence: `OrleansCommandDispatcher.cs:147-185` (the loop), `:137-145` (`Create`);
`MediatorCommandWorker.cs:170-203` (the four outcomes); `BusEnvelopeIntegrityVerifier.cs:61-91`;
`docs/guides/hmac-bus-envelope.md:38-52` (the event table and the rollout). Tests: the resumer under
`Off`, `Permissive` and `Strict` with a signed, an unsigned and a tampered record (fake store, the shape
of `RecordedCommandDrainTests`), asserting hand-over, keep, reason and event id per case; an
integration test that records a command in strict mode, updates the row's session JSON in PostgreSQL,
kills the host and asserts the command is kept with the reason and the handler never ran.

### D3 — The drain passes again while a pass was full, for at most its period

`OutboxDrainWork.ResumeRecordedCommandsAsync` loops: a pass; if the pass's due batch was full
(`GetDueAsync` returned `BatchSize` rows) and the token is not cancelled and the run has lasted less than
`PollingInterval` since it began, another pass at once; otherwise the run ends and the next run
continues. The resumer returns whether its batch was full beside the count it handed over. A claimed
command is not due again for a grace and a kept one not at all, so consecutive passes walk the backlog
rather than re-read it; the time bound keeps a run from holding the singleton grain across a silo's
stop and honours the outbox rule that a pass never depends on storage coming back empty.

*Rejected: a larger default batch.* It changes the per-run cost for every host to serve the outage
case, and still bounds the backlog at one batch per period.
*Rejected: a run without a bound.* A handler that makes every command fail keeps every command due
after a grace; the run would loop until the attempt bound keeps them all.

Evidence: `OutboxDrainWork.cs:30-50,59-81,134-137`; `OrleansCommandDispatcher.cs:147-185`;
`openspec/specs/outbox-and-messaging/spec.md` (*A worker drains durable storage in batches*). Tests: a
unit test on the drain with a fake resumer that reports full batches — passes until the first short
one, and at most until the period has elapsed under a fake clock; an integration test that records
five batches' worth, lets the grace pass, and asserts every command handed over within one period.

### D4 — A batch is claimed in a bounded number of statements, through a port member with a default

`ICommandIntentStore` gains `Task<IReadOnlyList<Guid>> ClaimAsync(IReadOnlyList<RecordedIntent> due, DateTimeOffset now, CancellationToken)`
with a default implementation that calls `TryClaimAsync(intent.Id, intent.LastHandedOverAt, now)` per
row and returns the ids claimed — correct for any store, and what a consumer's own store gets without
a change. The EF store overrides it in two statements: one `ExecuteUpdateAsync` over the due ids that
stamps `now` and increments the attempt count where the row is not kept and its last hand-over is
still null or no later than the latest last hand-over among the rows read — the set-based form of the
per-row equality guard, and as safe: a concurrent claimer stamps its own `now`, which is later than
anything the read returned, so the row drops out of the set — and one read of the ids whose last
hand-over now equals `now`, which are the ones this call stamped. The resumer keeps the exhausted
rows first, as today, then claims the rest in one call and hands the claimed ones over in the order
they were read.

Why `now` as the claim mark: the drain is singleton work, its passes are sequential, and `now` is the
value this pass wrote; a second claimer with the identical instant would also be handing the same
rows over, which is the at-least-once the handlers already tolerate.

*Rejected: `RETURNING` in one statement.* Provider-specific; the intent store is written against the
framework's write context for any provider.
*Rejected: changing `TryClaimAsync`.* It stays as the per-row primitive the default is built on and a
consumer's store may already implement.

Evidence: `CommandIntentStore.cs:40-79`; `OrleansCommandDispatcher.cs:150-166`;
`ICommittedPositionReader.HeadAsync` (the default-member precedent). Tests: the default against a fake
store claims each row through `TryClaimAsync`; an integration test that counts the statements of one
pass of a hundred due rows against PostgreSQL through an EF command interceptor (a handful, not a
hundred) and asserts every row claimed once with its attempt count incremented.

## Risks / Trade-offs

- [A host rolls to strict before its 4.1.1 recorders are gone] → unsigned records are kept, not lost;
  the guide's rollout says permissive until 117_114 falls to zero, then strict, exactly as for the bus,
  and a kept record is returned with the documented statement.
- [A tampered record is kept, not deleted] → keeping is the documented, visible outcome; deleting
  would erase the evidence.
- [The pass loop runs a run for a whole period under a large backlog] → intended; the singleton grain
  never overlaps runs, and the time bound ends the run on a silo's stop.
- [A drain-only silo has no signer or mode registered] → its verification reports skipped, as the bus
  spec requires where no signer is registered, and every signed record is resumed unverified; the
  migration guide's outbox-worker row, which already tells the drain silo to carry the API host's
  grace and attempt bound, gains the signer and the mode.
- [A consumer's own `ICommandIntentStore` keeps the per-row claim] → the default is correct and no
  slower than today; the member's remarks say what the override buys.
- [Two changes allocate the same log event ids] → assigned at implementation as the next free ones in
  the band; the schema page is the record.

## Migration Plan

Patch release. New public surface: `ICommandIntentStore.ClaimAsync` with a default; four log event
ids. No schema change. Rolling upgrade: a 4.1.2 drain verifies records from 4.1.1 recorders as
unsigned — run permissive until they have drained (one grace after the last 4.1.1 recorder stops),
then strict; a 4.1.1 drain ignores the signature a 4.1.2 recorder writes. Rollback: a signed record is
an ordinary record to 4.1.1.
