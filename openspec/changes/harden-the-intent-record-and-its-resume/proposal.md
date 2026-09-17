# harden-the-intent-record-and-its-resume

> **Status:** proposed

## Why

The round-5 audit read the recorded command — the row the execution model writes before a dispatch
returns and reads back after a crash — as it read a bus message, and found two things the bus path
settled long ago and this path never did (findings **R5-Cmd-008** and **R5-Cmd-010**, both low):

- **The record is unsigned.** On the bus path a command envelope is signed over its identity claims —
  type name, session context, heavy flag, a digest of the body — when the host has a signer, and
  every consumer verifies it under the host's integrity mode, because the session context decides
  which tenant is written to. The recorded command carries the same envelope with the same fields,
  serialised as plain JSON into the outbox table, and is resumed after a crash under whatever session
  the row now says. The row lives in the write store, which is the trust domain the store already
  is; but a host that signs its bus messages because it does not trust everything with write access
  to its infrastructure gets no such protection for the one message that is written to storage on
  purpose, and the threat model of the two paths diverges without anyone having decided that.
- **A backlog is resumed at `BatchSize / PollingInterval`.** The drain resumes one batch per run — a
  hundred commands every five seconds by default, twenty a second — and stops, however many are due;
  a host that comes back from an outage with ten thousand recorded commands resumes them over eight
  minutes while its handlers idle. Inside the batch every claim is a round trip of its own — read the
  due rows, then one guarded update per row — so the pass costs a hundred and one statements for a
  hundred commands, and a larger batch buys throughput only in proportion to the round trips it adds.

## What Changes

- **The recorded command is signed as a bus envelope is, and verified before it is resumed.** Where the
  host has registered a bus-envelope signer, the record carries the signature over the same canonical
  form the bus uses, and the drain verifies it under the host's integrity mode before handing the
  command over. Under strict mode a record that carries no signature, or one that does not verify, is
  kept for an operator at once, with the reason recorded, without an attempt — a retry cannot change
  the answer; under permissive mode it is resumed and the failure recorded; off verifies nothing. The
  two failures are distinct log events, as they are on the bus, so an operator can alert on a tampered
  record without alerting on an unsigned one. A command handed over live, without passing through
  storage, is not verified — it never left the process. Records written before the host signed are
  unsigned: the documentation names the same rollout the bus uses, permissive until the unsigned ones
  have drained, then strict.
- **A backlog is resumed as fast as the handlers take it.** While a pass finds as many due commands as
  it asked for, the next pass follows at once, until a pass finds fewer or the run has lasted its own
  period; the next run continues. A pass claims its batch in a bounded number of store round trips
  whatever the batch size, through a new member on the intent-store port with a default a consumer's
  own store keeps compiling on.
- **Consumer-visible effects:** a signed and verified record on hosts with a signer; a kept command
  with a new reason under strict mode; four new log event ids; one new member with a default on a
  public port; a backlog drains in seconds rather than minutes. No schema change — the signature is a
  field the stored envelope already has. Versioning: patch.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *An accepted command is recorded before the call returns and resumed after a
  crash* — the record is signed where the host signs, verified under the host's mode before it is
  resumed, kept with the reason under strict; a backlog is not bounded by the drain's period and a
  pass's round trips do not grow with its batch; new scenarios.
- `bus-envelope-integrity`: *Verification applies on every path that consumes a bus message* — a
  command resumed from its record is verified as a command from the bus is; new scenario.

## Impact

- `Stratara.Orleans` — `src/Stratara.Orleans/Aggregates/IntentRecorder.cs` (signing),
  `src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs` (`IntentResumer`: verification, the
  batch claim), `src/Stratara.Orleans/Singleton/OutboxDrainWork.cs` (the pass loop),
  `src/Stratara.Orleans/Diagnostics/OrleansLog.cs` (four events).
- `Stratara.Abstractions` — `src/Stratara.Abstractions/Abstractions/Outbox/ICommandIntentStore.cs`, one
  member with a default.
- `Stratara.Orleans.EntityFrameworkCore` — `src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs`
  (the batch claim).
- `Stratara.Diagnostics` — `src/Stratara.Diagnostics/LogEvents.cs`, four ids in `LogEvents.Orleans`.
- `docs/guides/operate-the-orleans-execution-model.md` (*Kept commands*: the two new reasons; *What to
  watch*), `docs/guides/hmac-bus-envelope.md` (the recorded command as a fourth consumer in the event
  table and the rollout), `docs/guides/migrate-to-the-orleans-execution-model.md` (the outbox-worker
  row: the drain's throughput), `docs/reference/log-events-schema.md`,
  `src/Stratara.Orleans.EntityFrameworkCore/README.md`, `CHANGELOG.md`, `llms.txt`.
- Tests: `tests/Stratara.Orleans.Tests/` — the recorder with and without a signer, the resumer under the
  three modes with an unsigned and a tampered record, the drain's pass loop, the port's default;
  `tests/Stratara.Orleans.IntegrationTests/Aggregates/` — a record altered in PostgreSQL then resumed, a
  backlog of several batches resumed within one period, the statements of one pass.
- Versioning: patch.
