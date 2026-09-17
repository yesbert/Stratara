## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. The defects, reproduced

- [ ] 1.1 A recorded command whose stored session is altered is resumed and runs under the altered
      session: an integration test records a command with a signer in strict mode, updates the row's
      session JSON in PostgreSQL, kills the host and expects the command kept with the reason. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Aggregates/SignedIntentTests.cs` (scenario *A recorded
      command's session is altered in storage*); run against 4.1.1 first and record here that the handler
      ran under the altered session.
- [ ] 1.2 A backlog of five batches is resumed one batch per period: an integration test records
      `5 × BatchSize` commands, lets the grace pass, and expects every one handed over within one
      `PollingInterval`. Verify: `tests/Stratara.Orleans.IntegrationTests/Aggregates/ResumeBacklogTests.cs`
      (scenario *A backlog larger than one batch is due*); run against 4.1.1 first and record the five
      periods it took.

## 2. The record is signed (D1)

- [ ] 2.1 `IntentRecorder` takes an optional `IBusEnvelopeSigner` and records the envelope with the
      signature over `BusEnvelopeCanonical.Of(envelope)` when one is registered. Verify:
      `src/Stratara.Orleans/Aggregates/IntentRecorder.cs`; `tests/Stratara.Orleans.Tests/IntentRecorderTests.cs`
      — with a fake signer the recorded envelope carries the signer's answer for its canonical form,
      without one it carries `null`.

## 3. The resume verifies (D2)

- [ ] 3.1 Four ids in `LogEvents.Orleans` at the next free positions in the band (117_114–117_117 as of
      #109) — `IntentUnsignedResumed`, `IntentIntegrityResumed`, `IntentUnsignedKept`, `IntentIntegrityKept`
      — and the four `OrleansLog` methods, each with the intent id. Verify: `src/Stratara.Diagnostics/LogEvents.cs`,
      `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`; `DiagnosticsTests` lists each id once.
- [ ] 3.2 `IntentResumer` takes the optional signer and `IOptions<BusEnvelopeIntegrityOptions>` (also through
      `Create` on a drain-only silo) and verifies each due record before claiming: strict → kept at once
      with the reason and the kept metric, no attempt; permissive → logged and resumed; off → as today.
      Verify: `src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs`;
      `tests/Stratara.Orleans.Tests/IntentResumerIntegrityTests.cs` — nine cases (three modes × signed,
      unsigned, tampered) asserting hand-over or keep, the recorded reason and the event id (scenario *A
      record written before the host signed is resumed*).
- [ ] 3.3 The test of 1.1 passes; a kept record returned with the documented statement is verified again
      and resumed once the row is signed correctly. Verify: `SignedIntentTests` — a second case that
      restores the session JSON, returns the command, and expects it applied.

## 4. The backlog drains (D3, D4)

- [ ] 4.1 `ICommandIntentStore.ClaimAsync(IReadOnlyList<RecordedIntent> due, DateTimeOffset now, CancellationToken)`
      with a default over `TryClaimAsync`, documented with what an override buys. Verify:
      `src/Stratara.Abstractions/Abstractions/Outbox/ICommandIntentStore.cs`; `tests/Stratara.Orleans.Tests/`
      — the default against a fake store claims each row through `TryClaimAsync` and returns the ids claimed.
- [ ] 4.2 `CommandIntentStore<TContext>.ClaimAsync` in two statements: the set-based guarded update and the
      read of the ids stamped with `now`. Verify: `src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs`;
      `tests/Stratara.Orleans.IntegrationTests/Aggregates/IntentClaimTests.cs` — a hundred due rows claimed
      in a pass whose statements an EF interceptor counts (below ten), every row stamped once with its
      attempt count incremented, a row stamped concurrently before the call left unclaimed.
- [ ] 4.3 `IntentResumer.ResumeDueAsync` keeps the exhausted rows, claims the rest in one `ClaimAsync`, hands
      the claimed ones over in read order, and reports whether the batch was full. Verify:
      `OrleansCommandDispatcher.cs`; `RecordedCommandDrainTests` still green.
- [ ] 4.4 `OutboxDrainWork.ResumeRecordedCommandsAsync` passes again while the last pass was full, until a
      short pass, cancellation, or the run has lasted `PollingInterval`. Verify: `src/Stratara.Orleans/Singleton/OutboxDrainWork.cs`;
      `tests/Stratara.Orleans.Tests/OutboxDrainPassLoopTests.cs` under a fake clock — three full passes then
      a short one end the run after four; full passes end the run once the period has elapsed.
- [ ] 4.5 The test of 1.2 passes. Verify: `ResumeBacklogTests` green; the time it took recorded here.

## 5. Documentation

- [ ] 5.1 `docs/guides/operate-the-orleans-execution-model.md` — *Kept commands*: the two integrity reasons
      and that returning such a command verifies it again; *What to watch*: the four events, alert on the
      invalid ones; the drain's pace under a backlog. Verify: the sections; documentation tests.
- [ ] 5.2 `docs/guides/hmac-bus-envelope.md` — the recorded command as a consumer in the event table (the
      four ids) and one sentence in the rollout paragraph. Verify: the table.
- [ ] 5.3 `docs/guides/migrate-to-the-orleans-execution-model.md` (the outbox-worker row: the backlog pace),
      `docs/reference/log-events-schema.md` (four rows), `src/Stratara.Orleans.EntityFrameworkCore/README.md`
      (the claim), `CHANGELOG.md` `[Unreleased]` *Added* (signing and verification, `ClaimAsync`, the
      four ids) and *Changed* (the pass loop), `llms.txt`. Verify: the entries; doc-symbol check.

## 6. Close

- [ ] 6.1 `./scripts/local-gauntlet.sh` green; the `Aggregates`, `Outbox` and `Singleton` integration
      namespaces green against PostgreSQL, Redis and RabbitMQ. Verify: the run output, recorded here.
- [ ] 6.2 `openspec validate harden-the-intent-record-and-its-resume --strict` passes. Verify: the output.
