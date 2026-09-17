# Tasks

## 1. The resume

- [x] 1.1 Hold the row's identity and heavy claim to the signed envelope, for the records the model
      wrote itself; a disagreement is kept under strict mode with the reason recorded and resumed by the
      signed claim under permissive mode, logged as `117_122`.
- [x] 1.2 Route the hand-over by the signed heavy claim.
- [x] 1.3 Unit tests for both modes, and the drain's fixtures record envelopes as the recorder writes
      them.

## 2. The stop

- [x] 2.1 `RunAcceptedAsync` abandons what is queued when it ends on the stop; `Accept` gives up a
      command that arrives afterwards.
- [x] 2.2 The deactivation's wait catches whatever it ends with, so the rest of it runs.
- [x] 2.3 Integration test: the callers of commands queued behind a stopped handler are answered when
      the silo stops, and none of those commands runs.

## 3. The timer and the claim

- [x] 3.1 The tick's renewal check and unregister run under the grain's gate.
- [x] 3.2 Write down what the claim's millisecond stamp can and cannot tell apart, and why the
      truncation stays.

## 4. Documentation and the run

- [x] 4.1 The bus-envelope guide says what a record's signature covers and what it does not.
- [x] 4.2 `CHANGELOG.md` under `[Unreleased]` → `Fixed`.
- [x] 4.3 Gauntlet, the intent unit tests and the stopping-silo, timer and intent integration suites.
