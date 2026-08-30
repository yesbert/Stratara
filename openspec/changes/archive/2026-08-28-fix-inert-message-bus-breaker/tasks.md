# Tasks

## Decide first

- [x] **`MinimumThroughput`: five or ten? → **five**, decided 2026-08-28.** Five means the circuit opens after roughly five minutes
      of continuous failure, ten after roughly ten. `design.md` recommends five and says why. Record
      the answer here before touching the factory — the whole defect is three numbers chosen without
      reference to one another, and picking a fourth the same way would be the same mistake.

## Implement

- [x] Size the breaker's window from the retry's cap in `CreateMessageBusPipeline`
      (`src/Stratara.Resilience/Resilience/ResilienceFactory.cs`): `SamplingDuration` derived as
      `MaxDelay × MinimumThroughput × 2`, with the resulting literal values stated in a comment so a
      reader does not have to evaluate the expression.
- [x] Leave `FailureRatio` at 1.0 and `BreakDuration` at 60s. The open state is there to be seen,
      not to suppress traffic — suppression is the backoff's job, and a longer break would only slow
      recovery.
- [x] Correct the `<remarks>` block. It currently states the breaker "opens after 10 consecutive
      failures within 60 s", which has never happened. Say what the settings now do, and why the
      window is tied to `MaxDelay`.

## Tests

- [x] Replace `TheCircuitBreakerNeverOpens_BecauseTheBackoffOutrunsItsSamplingWindow` with a test
      asserting the circuit **opens** under sustained failure, observed through Polly telemetry on a
      controlled clock. Do not delete the old test silently: it exists so this change has something
      that fails when it lands.
- [x] Assert the invariant directly — the sampling window is at least `MaxDelay × MinimumThroughput`
      — so a future edit to any one of the three settings fails at the point of change rather than
      by an operator noticing an alert stopped firing.
- [x] Assert the circuit closes again once the operation starts succeeding, so the recovery half of
      the requirement is covered and not just the opening half.
- [x] Confirm the existing message-bus retry test still passes unchanged: this change must not alter
      that retries are unbounded.

## Rollout

- [x] CHANGELOG entry. The observable outcome does not change — the retry wraps the breaker and
      retries `BrokenCircuitException` too, so traffic still succeeds on recovery — but logs and
      metrics change shape for anyone whose broker is down for minutes, and an alert on breaker
      state becomes possible for the first time. Say both.
- [x] Note in the entry that audit finding **F-005**'s stated protection was never in force until
      this change. A reader who trusted the old comment made a decision on false information.

> **Deviation, recorded rather than absorbed.** The last task asks the CHANGELOG entry to name audit
> finding **F-005**. `docs/versioning.md` → *CHANGELOG voice* forbids internal finding IDs in public
> entries by name, `F-001..F-028` included. The entry carries the substance — that the framework
> documented a protection it did not have — without the identifier. The convention wins over the task.
