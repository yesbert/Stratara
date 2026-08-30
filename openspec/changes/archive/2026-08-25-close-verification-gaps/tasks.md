# Tasks

- [x] **TE-2** — a known-input/known-output test for `EventStreamHashService.ComputeHash`, pinning
      field order, separator and encoding. Highest value of the six.
- [x] **SG-2** — add `event_source.append.conflicts` to
      `All_observability_instruments_are_published_on_the_shared_meter`; add recording-site tests
      for the instruments that have none (eight of nine).
- [x] **ES-1** — a test appending to a stream whose recorded owner differs from the session's
      data owner, asserting the payload is encrypted under the *stream's* owner.
- [x] **P-1** — a dependency-graph assertion over the built assemblies: no Tier-A package references
      a Tier-B or Tier-C package, no cycles. Belongs in `Stratara.SmokeTests`.
- [x] **R-2** — failure-path tests for the message-bus policy (retry and circuit breaker) and the two
      dispatcher policies (attempt bound).
- [x] **H-2** — a test asserting `QueuedHostedService` processes with more than one worker loop.
- [x] **AR-1** — the aggregate public-setter constraint has no mechanical enforcement and no
      diagnostic: a violating aggregate rebuilds successfully and silently loses the state captured
      before its most recent snapshot, so the damage grows the better snapshotting works. An
      analyser, or a start-up scan over registered aggregate types, would close it. This is an
      enforcement gap rather than a missing test, and belongs here for the same reason: the
      requirement is true and nothing can falsify a violation.

## What the work turned up

- [x] **R-2 found a defect, not a confirmation.** The message-bus circuit breaker cannot open. It
      needs ten actions inside a sixty-second sampling window; the retry backoff caps at sixty
      seconds, so once it has grown at most one failure lands per window. Measured, not reasoned:
      over a simulated hour of continuous failure, sixty-three attempts and no `OnCircuitOpened` at
      all. Nothing observable is broken — the duty cycle is bounded by the delay cap, which is what
      the breaker was credited with — but it is inert configuration and the code comment claims
      behaviour that does not happen. Pinned as a characterization test named for what it is; fixing
      the policy is a behaviour change and needs its own change.
- [x] **The tests were checked for whether they can fail.** Three were deliberately sabotaged. Two
      caught it. One did not: the first tier check (P-1) read assembly metadata, which omits
      references the compiler dropped — the very ones that still ship as NuGet dependencies. It reads
      the project files now. The first circuit-breaker attempt also passed against a pipeline with no
      breaker at all, which is what exposed the defect above.
- [x] **AR-1 needed a spec delta this change had not planned for.** It was filed as an enforcement
      gap, and enforcing it is a behaviour change: the host now refuses to start rather than losing
      state quietly. That makes the existing `aggregate-rehydration` scenario false, so `skip_specs`
      is gone and the requirement is amended.
- [x] **The AR-1 guard exempts computed properties.** Writing the test surfaced it: a property with
      no backing field is recomputed after a restore and loses nothing, so demanding a setter for it
      would force consumers to break working code. Only state-holding properties are flagged.
