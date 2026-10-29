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

## Migration Plan

Nothing to migrate. Silos of 4.6.0 and of this release mix: the change is local to the silo that runs the attempt.
