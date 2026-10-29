## Context

See proposal.md, *Why*. The code as of `f76fa6d` (4.6.0):

- `CommandExecution.RunIntentAsync` (`src/Stratara.Orleans/Aggregates/AggregateGrain.cs`) runs a recorded command
  under its `IntentLease`. It calls `InvokeAsync` once, inside `around` (heavy work acquires its permit there). On a
  failure it calls `lease.RecordFailureAsync`, which records a `ConcurrencyException` through
  `ICommandIntentStore.RecordConflictAsync` (one conflict counted, the attempt given back) and anything else through
  `RecordFailureAsync`. It then rethrows, and the lease ends.
- The drain resumes a record only when `Timestamp` *and* `LastHandedOverAt` are older than `IntentGrace`
  (`CommandIntentStore.GetDueAsync`). Moving the hand-over stamp back therefore does not make a fresh command due.
  That rules out "make it due now" as a fix.
- `IntentResumer.IsExhausted` keeps a record once `ConflictCount > MaxConflictRequeues`. The test *A failing handler
  runs as often as the delivery bound and a conflicting one until the conflict bound*
  (`ResumedOnceInOrderTests`) asserts exactly `MaxConflictRequeues + 1` runs.
- `ResilienceNames.ConcurrencyConflict` is registered by `AddResiliencePipelines()`, which the composites call. It
  retries `ConcurrencyException` and `ConcurrencyConflictException` five times from 50 ms, exponentially with jitter.
- The command's first attempt runs in the scope its hand-over created: the aggregate grain's intent scope, or the
  scope `CommandExecution.RunAsync` creates for a heavy command.

## Goals / Non-Goals

**Goals:**

- A conflicting recorded command runs again within moments, on the silo that ran it.
- The number of runs of a command that always conflicts stays what it is today.

**Non-Goals:**

- Faster resumption after a host died. That stays the grace's job.
- Retrying failures other than the event store's `ConcurrencyException`. A provider's own
  `ConcurrencyConflictException` stays an ordinary failure on this path, as `IntentFailure.IsConflict` defines it.

## Decisions

### D1 — Run again inside the hand-over, through the framework's conflict pipeline

`RunIntentAsync` runs the attempts through the pipeline named `ResilienceNames.ConcurrencyConflict`, resolved with
`ResiliencePipelineProvider<string>.TryGetPipeline`. Without it there is one attempt, as today. Each attempt after
the first runs in a scope of its own from the root `IServiceScopeFactory`, so the handler reads the stream again in a
fresh unit of work, and the scope is disposed after the attempt. The pipeline sits inside `around`, so a heavy
command holds its permit across the attempts, and inside the lease, so the hand-over keeps renewing.

Only the event store's `ConcurrencyException` reaches the pipeline as itself. Every other exception, including a
provider's `ConcurrencyConflictException`, leaves the attempt wrapped in a private pass-through exception that the
pipeline does not handle. It is unwrapped outside and handled as today.

*Evidence:* the delays and the handled types in `ResilienceFactory.CreateConcurrencyConflictPipeline`, and the
attempt's scope as `RunIntentAsync` receives it.

*Alternatives:* re-hand the command over through a claim and the hand-over path, as the drain does. That runs off the
aggregate's turn and frees it meanwhile, but needs the claim's fencing against a drain, which is a second path to the
same hand-over, and it cannot claim without knowing the stamp the lease last renewed. Rejected as more machinery than
the fix needs. Making the record due at once was rejected because of the record-time condition in `GetDueAsync` (see
*Context*).

### D2 — The conflict bound decides whether there is another run

Inside the attempt, a conflict is recorded through the new `ICommandIntentStore.CountConflictAsync`, which records it
as `RecordConflictAsync` does and answers the record's conflict count afterwards. The attempt rethrows the conflict to
the pipeline, so the command runs again, only while `count <= MaxConflictRequeues`. That is the condition under which
`IsExhausted` would still resume it. Otherwise, or when the count is `null`, the conflict leaves through the
pass-through, and the drain keeps or resumes the command as today. A command that always conflicts therefore runs
`MaxConflictRequeues + 1` times in all, the same number as before, only sooner. The last conflict is recorded once:
`RunIntentAsync` remembers the exception it recorded and does not record it again in its outer handler.

`CountConflictAsync` is a default interface member that calls `RecordConflictAsync` and answers `null`, so a store of
the host's own compiles and behaves as before. `CommandIntentStore` implements it with the existing update and a read
of `ConflictCount`.

*Alternative:* bound the reruns by the pipeline alone. That breaks the run count the existing test pins, and lets a
command exceed the bound by up to five runs per hand-over.

### D2a — The attempt is given back once (revised after the code review of 2026-10-29)

The first plan recorded every conflict through `RecordConflictAsync`, which gives the hand-over's attempt back each
time, so reruns wore the delivery bound down, and a stop during a pause gave the attempt back twice. Now
`CountConflictAsync` counts the conflict *without* giving the attempt back, and `RunIntentAsync` gives it back once
through `ReturnAttemptAsync` when the command leaves the hand-over on a conflict. A rerun that ends in another failure
keeps the attempt counted and records the failure, and a stop gives it back once, as before. A store whose
`CountConflictAsync` is the default records through `RecordConflictAsync` and answers `null`: then the attempt was
given back already, and nothing is given back again. The bound check is one predicate,
`IntentFailure.WithinConflictBound`, which the drain's `IsExhausted` and the rerun share.

### D1a — The reruns continue on the activation's scheduler (found by the code review of 2026-10-29)

Polly continues after its delay without the captured context unless the `ResilienceContext` asks for it. The
pipeline therefore runs with a pooled context with `ContinueOnCapturedContext = true`, so a rerun of an aggregate's
command runs on the aggregate activation's scheduler, as the first attempt did. *Evidence:* `ConflictRerunTests`
records `TaskScheduler.Current` per run; with the flag off the rerun ran on the thread pool (`[True, False]`), and
with it on, both ran on the activation.

### D3 — Logging and counting stay per attempt

Every failed attempt is logged through `LogIntentAttemptFailed`, as today. A silo stop during the pipeline's pause
cancels it through `stopping`, which ends the run as a stop and gives back the attempt as today.

## Risks / Trade-offs

- **The aggregate's turn is held during the pauses**, which add up to at most about 1.5 s with the pipeline's
  defaults. → Commands queued behind it wait that long. That is far less than the 30 s grace they would otherwise
  wait for the conflicting one.
- **A host that replaced the named pipeline changes the reruns.** → Intended: the pipeline is the host's knob, and
  the guide names it.
- **A provider's `ConcurrencyConflictException` is still not rerun.** → That is unchanged behaviour, and called out in
  the guide.
- **A small conflict bound is used up within moments** (review finding). A command that collides with a writer that
  keeps appending for seconds reaches a bound of 3 within about a second and is kept. → That is what the bus does,
  which redelivers a conflicting message at once. The guide says so, and a host that wants more patience raises
  `MaxConflictRequeues`.
- **A command that also carries the concurrency-conflict pipeline through the mediator's resilience behaviour**
  retries inside each run, so up to six times six runs per hand-over, with only the outer conflicts counted (review
  finding). → That is a rare composition. The guide names it.
- **Per-command cost** (review finding): every recorded command now resolves the named pipeline and takes a pooled
  `ResilienceContext`. → A dictionary lookup and a pooled object per command, against a handler that writes to the
  store. Not measured, judged negligible.

## Migration Plan

Nothing to migrate. Silos of 4.6.0 and of this release mix: the change is local to the silo that runs the attempt.
