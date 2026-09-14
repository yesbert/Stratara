# Optimise the Orleans execution model

> **Status:** approved

## Why

`prove-an-orleans-execution-model` (archived 2026-09-13) recommended Orleans as an *additional*
execution model and not as the default, on three measured costs: the silo spends **59 % more
processor time per command** than the bus worker (B5), a rebuild of one projection takes **94 % of
a full replay** (B4), and one aggregate under load runs at **87 % of the bus worker** on the
durable-intent shape (B3). Reading the proof-of-concept code against those numbers shows that none of
the three is a property of Orleans: the rebuild resumes its sixteen partitions one after another, a
command pays two database contexts and a directory round trip it does not need, and every wake-up
hint is a full turn with its own checkpoint read. The recommendation was made on costs that are
ours to remove, and the question whether Orleans should become the recommended model cannot be
asked again until they are.

## What Changes

- The projection rebuild resumes every partition concurrently and no longer awaits a partition's
  whole catch-up inside `ResumeAsync`; a batch is applied under one scope, one projection instance
  and one relevant-event set instead of one of each per entry; the proof-of-concept store model
  gains an expression index so a partition read does not scan the other fifteen partitions.
- The durable-intent dispatcher completes intents in batches — one delete statement per short
  window instead of one context and one round trip per command — and the command envelope is
  marked immutable so a local grain call does not deep-copy it.
- The wake-up hint coalesces: a nudge that arrives while a catch-up runs marks the grain dirty
  instead of queuing another catch-up with its own checkpoint read; the grain keeps its position
  across catch-ups and reads the checkpoint store on activation only; the checkpoint is written with
  one upsert.
- Two things are measured rather than decided: the grain directory for aggregate grains (Redis, as
  the proof of concept chose, against the built-in directory), and the membership settings that
  govern how long a silo restarted on the same endpoint waits for its predecessor to be declared
  dead.
- The benchmark silo gets a production-shaped profile — default reminder refresh, default drain
  interval — so that it pays what a deployed silo pays and not what the kill-and-restart tests
  need; the tests keep their fast profile.
- The findings an operator needs — a silo that dies hard and a replacement on another address,
  the directory per grain type, the profile, what the bus path costs since 4.0.4 — are written up
  in `evidence/operations-note.md`, beside the archived migration note.
- Every measurement of the archived change that named one of these costs is re-run with the bus
  path as the in-run control, its raw output committed in this change's `evidence/`, and its result
  compared with the archived number. Expectations are written before the first run.

## Consumer-visible effect

**None.** `src/Stratara.Orleans` stays non-packable and out of `Stratara.Publish.slnf`; no
published package gains, loses or changes a member; no composite registers anything different; no
version moves; nothing is published. The guarantees the proof of concept was measured against —
`projections`, `sagas`, `outbox-and-messaging`, `mediator-dispatch` — are not touched and must
still hold on the Orleans path after every optimisation: the existing integration tests are the
check.

What does change inside the proof of concept, and would matter to a change that ships it:

- an intent completed by the handler may stay recorded for up to one completion window before its
  row is deleted; a host that dies in that window resumes the intent and runs the handler a second
  time, which the at-least-once contract of the durable-intent shape already allows;
- the store schema the proof-of-concept readers need gains an index; a shipping change carries it
  in its migration note like the columns it already has to carry.

## Capabilities

### New Capabilities

None. The change carries `skip_specs: true`: it makes measured code cheaper and records the
numbers. The Orleans path has no capability yet, and this change does not write one.

### Modified Capabilities

None. No requirement of any capability changes; the proof of concept must keep every guarantee it
kept before.

## Impact

- **Modified, non-packable:** `src/Stratara.Orleans/` — `Projections/ProjectionGrain.cs`,
  `Projections/ProjectionRebuilder.cs`, `Projections/StoreReaderLoop.cs`,
  `Projections/ProjectionCheckpoint.cs`, `Aggregates/AggregateGrain.cs`,
  `Aggregates/OrleansCommandDispatcher.cs`, `Aggregates/AggregateCommandEnvelope.cs`,
  `CommitOrder/CommitOrderModel.cs`, `CommitOrder/PostgresTransactionIdReader.cs`, and the
  registrations under `DependencyInjection/`.
- **Modified test support:** `tests/Stratara.Orleans.IntegrationTests/Hosting/PocSilo.cs` gains a
  profile parameter; `tests/Stratara.Orleans.Benchmarks/` gains a restart-delay run and uses the
  production profile.
- **New evidence directory:** `openspec/changes/optimise-the-orleans-execution-model/evidence/` —
  `expectations.md`, `raw/<measurement>/<timestamp>/`, `results.md`. It travels into the archive.
- **Not touched:** every packable project, every composite under `src/Stratara.Infrastructure/`,
  `Stratara.Publish.slnf`, `Directory.Packages.props`, `<VersionPrefix>`, `CHANGELOG.md`, every
  spec under `openspec/specs/`.
- **Superseded sources:** none. The archived results of `prove-an-orleans-execution-model` stay
  what they are — the numbers of that code on that day; `results.md` here cites them as the
  baseline and does not amend them.
