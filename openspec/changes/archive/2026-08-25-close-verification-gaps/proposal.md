> **Status:** proposed

# Close the verification gaps the backfill exposed

## Why

The backfill recorded, per requirement, whether it rests on a test, on read implementation code, or
on a document. Of 217 requirements the great majority are test-backed — but a handful of the most
consequential are not, and a requirement nobody can falsify is a statement rather than a guarantee.

This change carries the tests, not fixes: in every case the behaviour is believed correct and
nothing can prove it.

Ordered by what a regression would cost:

| # | Gap | What a silent regression would do |
|---|---|---|
| **TE-2** | No test pins the event-hash input | `ComputeHash` could change its field order, separator or encoding and the whole suite would pass — while every previously chained event became unverifiable. Tamper evidence would be gone and nothing would say so |
| **SG-2** | `event_source.append.conflicts` is the one instrument absent from the name-pinning test; eight of nine have no recording-site test | A renamed or unrecorded instrument breaks dashboards and alerts with no build or test failure |
| **ES-1** | No test covers event-payload encryption under the *resolved subject's* scope | The subject-resolution order is the store's subtlest behaviour; a defect where the stream's owner differs from the session's would encrypt under the wrong scope, and erasure would then miss it |
| **P-1** | Tier layering has no mechanical enforcement | A foundational package gaining a higher-tier reference would ship infrastructure dependencies to every lean consumer, discovered only by a consumer noticing them |
| **R-2** | Three of four resilience policies are covered only by a smoke test that never fails | The retry counts, backoff and circuit breaker in the spec rest on reading the factory |
| **H-2** | The background worker's parallelism is untested | Reducing it to a single loop would pass the entire suite |
| **AR-1** | The aggregate public-setter constraint has no enforcement and no diagnostic | A violating aggregate rebuilds successfully and loses the state its snapshot held — silently, and worse the better snapshotting works |

## What Changes

Tests, with one exception. Six of the seven gaps are closed by tests alone and change no behaviour.

**AR-1 is not a test.** It was recorded here as an enforcement gap, and closing it means the host now
refuses to start when a registered aggregate declares a state-holding property it cannot set. That is
a behaviour change, and it makes an existing scenario false: the `aggregate-rehydration` capability
currently states that such a property is not restored and the aggregate *silently* loses its state.
After this change it never gets that far. The change therefore carries a spec delta for that one
requirement, which it did not originally plan to.

**R-2 turned up a defect rather than confirming a guarantee.** The message-bus circuit breaker cannot
open: it needs ten actions inside a sixty-second sampling window, and the policy's own retry backoff
caps at sixty seconds, so at most one failure lands per window. Nothing observable is broken — the
duty-cycle bound the breaker was credited with is delivered by the delay cap — but the breaker is
inert configuration and the code comment describes behaviour that does not occur. Fixing it is a
behaviour change and belongs in its own change; this one pins the current state in a clearly labelled
characterization test so the next reader learns it in one test run.
