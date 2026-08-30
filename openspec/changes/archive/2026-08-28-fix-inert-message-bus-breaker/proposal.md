> **Status:** approved

# Make the message-bus circuit breaker able to open

## Why

The message-bus policy's circuit breaker cannot open. Not "rarely" — under its own retry policy it
is unreachable.

The breaker requires ten actions inside a sixty-second sampling window (`MinimumThroughput = 10`,
`SamplingDuration = 60s`). The retry in front of it uses exponential backoff capped at
`MaxDelay = 60s`. Once the backoff has grown, at most one failure lands per sampling window, so the
tenth is never counted. This was measured rather than reasoned: over a simulated hour of
uninterrupted failure the pipeline made sixty-three attempts and emitted **no** `OnCircuitOpened`
event at all. The test that found it is in `close-verification-gaps`, pinned as a characterization
test so the current state is visible.

Nothing a consumer can observe is broken today, and this is not a security issue. The
`resilience` capability guarantees that message-bus traffic eventually succeeds once the broker
recovers, and the retry delivers that on its own. The duty-cycle bound the breaker was credited
with — one attempt a minute rather than a retry storm — is delivered by the sixty-second delay cap.

What is wrong is that the framework carries configuration that does nothing, and a comment that
describes behaviour which does not occur:

> *"we wrap the retry in a circuit breaker that opens after 10 consecutive failures within 60 s and
> stays open for 60 s before half-opening — so a permanent failure surfaces in metrics + logs at
> roughly one breaker-cycle per minute instead of the unbounded retry storm the audit (F-005)
> flagged."*

A reader trusts that. An operator building an alert on breaker-state metrics gets an alert that can
never fire. And the next person to raise `MaxDelay`, or to shorten the backoff, has no way to know
they are changing whether a safety net exists.

## What Changes

The consumer-visible effect is that a permanently failing broker now surfaces as an **open circuit**
in metrics and logs, instead of only as a slow retry loop. Everything else about message-bus
behaviour stays as specified: retries remain unbounded, backoff remains exponential and jittered,
and traffic still succeeds once the broker recovers without the caller writing any retry.

- The breaker's thresholds are brought into a range its own retry can reach, so it opens under
  sustained failure. **This change does not settle which numbers** — see `design.md`; the owner
  decision is recorded there.
- The `resilience` requirement covering the message-bus policy gains a scenario stating that
  sustained failure opens the circuit. Today the requirement says the retry runs "behind a circuit
  breaker" without asserting that the breaker ever engages, which is why nothing caught this.
- The characterization test added by `close-verification-gaps` is replaced by a test asserting the
  breaker opens. That test failing is the intended signal that this change has landed.
- The code comment is corrected to match whatever the numbers end up being.

## Capabilities

### Modified Capabilities

- `resilience`: the requirement *Message-bus traffic retries indefinitely behind a circuit breaker*
  gains a scenario for the breaker actually opening under sustained failure, so the guarantee is
  falsifiable rather than descriptive.

## Impact

- `src/Stratara.Resilience/Resilience/ResilienceFactory.cs` — `CreateMessageBusPipeline`, both the
  thresholds and the `<remarks>` block that currently misdescribes them.
- `tests/Stratara.Shared.Tests/DependencyInjection/MessageBusResilienceTests.cs` — the
  characterization test `TheCircuitBreakerNeverOpens_BecauseTheBackoffOutrunsItsSamplingWindow` is
  superseded and must be replaced, not deleted quietly.
- `openspec/specs/resilience/spec.md` — the message-bus requirement.
- No package, tier or dependency change. `Microsoft.Extensions.TimeProvider.Testing` is already a
  test dependency, added by `close-verification-gaps`, and is what makes this testable at all.
- Supersedes the reasoning recorded against audit finding **F-005**, whose stated protection has
  never been in force.
