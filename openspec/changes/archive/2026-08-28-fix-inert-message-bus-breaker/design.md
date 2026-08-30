## Context

See `proposal.md` — *Why*. The numbers as they stand:

| Setting | Value | Where |
|---|---|---|
| Retry base delay | 10s | retry |
| Retry backoff | exponential + jitter | retry |
| `MaxDelay` | **60s** | retry |
| `MinimumThroughput` | **10** | breaker |
| `SamplingDuration` | **60s** | breaker |
| `FailureRatio` | 1.0 | breaker |
| `BreakDuration` | 60s | breaker |

The retry sits outside the breaker, so every attempt passes through it. Attempt times under
sustained failure are roughly t = 0, 10, 30, 70, 130, 190, … — after the fourth, one per minute.

That is the whole defect: **the breaker counts actions, and the retry supplies at most one per
sampling window.** Ten can never accumulate. The two settings were chosen independently and never
compared.

## Goals / Non-Goals

**Goals:**

- The breaker opens under sustained failure, so a permanent outage is visible as circuit state
  rather than only as a slow loop.
- The relationship between the retry's `MaxDelay` and the breaker's window is expressed so it cannot
  silently drift apart again.

**Non-Goals:**

- Changing what the retry guarantees. It stays unbounded, exponential and jittered.
- Using the breaker to throttle. The duty cycle is already bounded by `MaxDelay`, and that remains
  the mechanism. The breaker's job here is the signal.
- Any wire, package or tier change.

## Decisions

### Decision 1 — Fix the numbers rather than remove the breaker

**Chosen:** keep the breaker and make it reachable.

**Alternative considered — delete it and correct the comment.** Tempting, and honest: the duty-cycle
bound really does come from `MaxDelay`, so removing the breaker changes no behaviour whatsoever.
Rejected because it removes the only signal that distinguishes "the broker is briefly down" from
"the broker has been unreachable for ten minutes". That distinction is what an operator alerts on,
and audit finding F-005 asked for it. Deleting it would close the finding by abandoning it.

### Decision 2 — Size the window from the retry's own cap, in code

**Chosen:** derive `SamplingDuration` from `MaxDelay` and `MinimumThroughput` rather than writing
three unrelated constants, and assert the relationship in a test.

At steady state the retry produces one action per `MaxDelay`. For a window to contain
`MinimumThroughput` actions it must span at least `MaxDelay × MinimumThroughput`, and it needs
headroom because jitter spreads the attempts. Doubling gives that headroom:

```
SamplingDuration = MaxDelay × MinimumThroughput × 2
```

**Alternative considered — pick new literals (for example throughput 5, window 600s).** Same
behaviour, and simpler to read. Rejected as the primary form because it reproduces exactly the
condition that caused this defect: three numbers with an unstated relationship, any one of which can
be changed by someone who does not know about the other two. The derived form makes the dependency
visible at the point of edit. The literals it produces should still be stated in a comment so a
reader does not have to evaluate the expression.

**Owner decision, taken 2026-08-28: `MinimumThroughput` = 5.** The reasoning below stood; the owner took the recommendation. `MinimumThroughput`. Five gives an open circuit after roughly five
minutes of continuous failure (window 10 minutes); ten gives roughly ten minutes (window 20
minutes). Five is the recommendation — a broker down for five minutes is not transient by any
operational definition, and a shorter time to signal is worth more than a lower false-positive risk
here, because `FailureRatio` is 1.0 and a single success resets the count.

### Decision 3 — `BreakDuration` stays at 60s

The open state exists to be observed, not to suppress traffic — suppression is already the backoff's
job. A longer break would delay recovery after the broker returns without buying anything, since
half-open probes are the only thing that notices recovery.

### Decision 4 — Replace the characterization test, do not delete it

`close-verification-gaps` left `TheCircuitBreakerNeverOpens_BecauseTheBackoffOutrunsItsSamplingWindow`
in place precisely so this change has something that fails when it lands. It is replaced by a test
asserting the breaker opens, plus a test asserting the `SamplingDuration ≥ MaxDelay × MinimumThroughput`
invariant directly, so a future edit to any of the three settings is caught at the point of change
rather than by an operator whose alert stopped firing.

## Risks / Trade-offs

- **The breaker will now actually open in production**, which it never has. Any consumer whose
  message-bus traffic sees five consecutive failed minutes gets `BrokenCircuitException` surfacing
  where it previously saw only retries. The retry wraps the breaker and retries that exception too,
  so the observable outcome — eventual success on recovery — is unchanged. Worth a CHANGELOG note
  all the same, because logs and metrics change shape.
- **The derived expression is less readable than three literals.** Mitigated by stating the
  resulting values in a comment, and by the invariant test.
- **Time to signal is a judgement, not a derivation.** Five minutes is defensible, not provable.
  Recorded here so a future reader knows it was chosen rather than computed.
- **This is the third setting in this file whose value nothing verifies.** The dispatcher and
  concurrency attempt bounds are now pinned by tests from `close-verification-gaps`; after this
  change the message-bus settings are too. There is no fourth.
