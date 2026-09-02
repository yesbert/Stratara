# Design — Let no fact overtake its beginning

## Context

See `proposal.md` → *Why*. What matters here is the shape of what exists.

Three pieces of the command side are the template. `MediatorCommandWorker` runs
`CommandWorkerLane.EffectiveDegreeOfParallelism` subscriptions (`Stratara.Outbox.RabbitMQ/Mediator/CommandWorkerLane.cs`)
and, per command, takes a bucket lock keyed on the aggregate id before dispatching
(`MediatorCommandWorker.cs:134`). The lock is `BucketLockPool` in `Stratara.Mediator` — 4096
pre-allocated semaphores, one per bucket of `BucketCalculator.GetBucketId` — and it is `internal`.
`mediator-dispatch` → *Commands scoped to one aggregate are serialised against each other* is the
guarantee it carries, and `MediatorCommandWorkerTests` is the evidence.

The two workers this change touches have the parallelism and none of the rest:

```
Stratara.Projections/Services/ProjectionWorker.cs:74   Environment.ProcessorCount, hard-coded
Stratara.Sagas/Services/SagaWorker.cs:71               Environment.ProcessorCount, hard-coded
```

Both reference `Stratara.Shared`, where `BucketCalculator` and `BucketConstants` live, and through
it `Stratara.Abstractions`, where `IBucket` lives. Neither references `Stratara.Mediator`, and neither
should — it is a Tier-B package whose only purpose for them would be one internal class.

What a handler can signal today is one thing: `ConcurrencyException`, which `RabbitMqBus` maps to
`requeue=true` (`RabbitMqBus.cs:244`). Everything else is `requeue=false`. The Azure Service Bus
transport has its own mapping. Both are transport concerns, and neither is where this change acts.

Every bundle carries, per event, the `StreamId` and `AggregateTypeName` of the aggregate that
produced it (`Stratara.Contracts/Messages/EventMessage.cs`). A bundle from one command carries one
stream; the shape allows several.

The retry the framework already has for "try again shortly" is a named Polly policy:
`ResilienceNames.ConcurrencyConflict`, five attempts from 50 ms with exponential backoff and jitter,
handling only `ConcurrencyConflictException` (`ResilienceFactory.CreateConcurrencyConflictPipeline`).
The build guidance forbids hand-rolled retry loops; a new retry is a new named policy.

*An observation, not part of this change:* `projections` → *A failing projection stops the bundle*
says a failed bundle "is not acknowledged and is redelivered". On the RabbitMQ transport a failure
other than a concurrency conflict is rejected without requeue, which is not redelivery. The owner
should decide whether that requirement or that transport is right; this design does not depend on
the answer, because the retry it adds happens before the transport sees an outcome.

## Goals / Non-Goals

**Goals:**

- Within one worker process, two bundles about one aggregate never apply concurrently.
- A handler can distinguish "not yet" from "failed", and "not yet" gets a short second chance that
  cannot starve the fact it is waiting for.
- A host can choose how many consumers a worker opens, including one.
- The transports learn nothing new. Whatever a nack means today, it means tomorrow.

**Non-Goals:**

- Ordering across processes. Two replicas of the projection worker consuming one subscription still
  interleave, and this design says so rather than hiding it.
- Broker-side redelivery with a counter. See the decision below.
- Changing what `ConcurrencyException` means, or making the new exception a transport concern.
- Touching the command worker. It keeps its own lock pool; see the decision below.

## Decisions

### The lock pool moves to `Stratara.Abstractions`, and the mediator's copy goes

`BucketLockPool` moves from `Stratara.Mediator`, where it is internal, to
`Stratara.Abstractions.Partitioning` as a public type carrying its own `BucketCount` constant. It is
the one implementation the build guidance names for per-key serialisation, and every package that
needs it — the mediator, the projection worker, the saga worker — already references
`Stratara.Abstractions`. The mediator deletes its copy and the 4096 it mirrored, and
`BucketConstants.TotalBucketCount` in `Stratara.Shared` becomes a reference to `BucketLockPool.BucketCount`
rather than a second statement of the number. One pool, one count.

`Stratara.Abstractions` is the right home and not a stretch: it already carries `IBucket`, the
marker for anything partitioned this way, and it already carries implementation that every tier needs
— `EventUpcasterPipeline`, `TrustedTypeResolver`, `BusEnvelopeIntegrityVerifier`. A semaphore array
keyed by bucket is smaller than any of them.

*Rejected: a public copy in `Stratara.Shared`, with the mediator keeping its own.* It was the first
draft of this decision, on the model of the duplicated bucket count. Two forty-line copies of a lock
whose correctness depends on a shared constant is the kind of duplication the count was meant to be
the only instance of; with the pool in a package both sides already reference, the reason for the
count's own duplication disappears as well. The architecture note that records the mediator's
mirrored constant stops being true with this change and is retired with it.

*Rejected: have `Stratara.Mediator` reference `Stratara.Shared`.* The tiers allow it, and the
project already declined it once for one integer: `Stratara.Shared` depends on `Stratara.Sessions`,
which is ASP.NET-coupled, and the mediator would drag that into every consumer that only wants
dispatch.

*Rejected: move `BucketCalculator` and `BucketConstants` along with it.* Both are public in
`Stratara.Shared.Partitioning`; a consumer that uses them — a custom repository partitioning by the
same buckets — would face a namespace change, which is a major. That move, if it is ever wanted, is a
5.0 change and a separate one.

Evidence: `src/Stratara.Mediator/BucketLockPool.cs` and its comment on the mirrored constant, which
this change deletes; `src/Stratara.Abstractions/Abstractions/Entities/IBucket.cs` for the home.

### The lock key is every distinct stream in the bundle, acquired in ascending bucket order

A bundle normally carries one stream. When it carries several, the worker acquires the lock for each
distinct bucket, sorted ascending, and releases them in reverse. Sorted acquisition is what makes two
multi-stream bundles unable to wait on each other forever; it is the standard answer and it costs a
sort of a list that is almost always one element long. An empty bundle takes no lock.

*Rejected: lock on the first event's stream only.* Cheaper, and wrong for the bundle that touches two
aggregates — the second one is unprotected, and nothing would say so.

*Rejected: one lock per bundle regardless of content.* That is a degree of parallelism of one with
extra steps.

Evidence: the multi-stream scenario in the `projections` delta, and the worker test that carries it.

### "Not yet" is a published exception, handled by the worker, invisible to the transport

`Stratara.Abstractions.EventSourcing` gains `PrecedingFactMissingException(Guid streamId, string eventTypeName, Exception? innerException = null)`,
shaped like `ConcurrencyException` beside it: the stream and the event type the handler was applying
when it found the entity absent. A projection or saga throws it where it currently warns or throws
something generic.

The worker — not the bus — handles it. `HandleEventBundleAsync` runs the lock-and-apply step inside
the new named policy, so the retry happens before `SubscribeAsync`'s handler returns, and the
transport sees one outcome per bundle as it always did. Exhaustion lets the last exception propagate,
which is the existing failure path with a better message.

*Rejected: map the new exception to `requeue=true` in each transport.* That is the mechanism that
exists for `ConcurrencyException`, and it is why the proposal calls it "exactly one hard-coded
meaning". Classic queues have no delivery counter, so a requeue has no bound: a bundle whose beginning
never comes is redelivered forever, at the head of the queue, ahead of everything that could have
succeeded. Bounding it needs quorum queues or a re-publish with an attempt header — a topology
change or a wire-format change, and either one has to be done twice, once per transport. The
in-process retry is one implementation, transport-agnostic, and bounded by construction.

*Rejected: reuse `ConcurrencyException` for "not yet".* Its meaning is "another writer got there
first", its handling is a requeue, and overloading it would give the new case the unbounded
behaviour just rejected.

Evidence: `RabbitMqBus.cs:244-252` for the existing mapping; the `resilience` delta for the bound.

### The retry releases the lock between attempts

This is the decision that makes the other two work together. If the retry ran *inside* the lock, a
bundle waiting for its beginning would hold the very lock the beginning needs, wait out every attempt,
give up, and only then let the beginning through — a guaranteed loss instead of a probable one. The
policy therefore wraps the whole lock-and-apply step: each attempt acquires, tries, and releases;
each wait happens with nothing held.

The scenario *A waiting bundle does not block the fact it waits for* in the `projections` delta is
this decision stated as a guarantee, and the test for it is the one that must be written first.

### The policy is named, registered with the others, and short

`ResilienceNames.PrecedingFact` (`"PrecedingFactPipeline"`), built by `ResilienceFactory`, registered
in `AddStrataraResilience` alongside the existing four: five attempts, 100 ms base delay, exponential,
jittered — about three seconds in the worst case. It handles `PrecedingFactMissingException` and
nothing else.

Three seconds is chosen against the case it serves. A beginning that is in flight on a neighbouring
consumer is tens of milliseconds away. A beginning that is minutes away has a failed consumer behind
it, and holding a projection consumer for minutes to wait for it would stall every other aggregate
that consumer would have served. The `resilience` delta states the bound without the numbers; the
numbers live here and in the factory, next to the concurrency policy's.

*Rejected: make attempts and delay configurable in `ProjectionOptions`.* None of the other named
policies are, and a consumer that needs different numbers overrides the named policy, which is the
mechanism `resilience` → *A request opts in to a policy and names which one* already offers.

Evidence: `ResilienceFactory.CreateConcurrencyConflictPipeline` for the shape being mirrored.

### The option mirrors the heavy lane, in the options that already exist

`ProjectionOptions.DegreeOfParallelism` and `SagaOptions.DegreeOfParallelism`, both `int?`, both bound
from the sections the options already bind from (`Projections`, `Sagas`), both with the lane's
fallback: not a positive number means `Environment.ProcessorCount`. The scenario wording in
`outbox-and-messaging` → *A heavy worker runs* is reused verbatim so the three surfaces cannot drift.

*Rejected: a constructor parameter on `AddProjectionWorker`.* The heavy lane does it that way because
it has no options object. These two do, and a configuration-bound number is what an operator can
change without a deploy.

Evidence: `CommandWorkerLane.EffectiveDegreeOfParallelism` and `CommandWorkerLaneTests`.

### Sagas get all three, not just the lock

The saga worker is the same code with a different manager. A saga that reads a view before it
dispatches can hit the same race, and a design that gave projections a "not yet" and sagas nothing
would make a saga author reach for `ConcurrencyException` — the overload rejected above. The cost is
a second copy of a small change and a second set of tests; the alternative is an asymmetry nobody
would be able to explain in a year.

### What this serialises, and what it does not order

The lock guarantees that two bundles about one aggregate do not *overlap*. It does not guarantee
which goes first. In the ordinary case the consumer that received the creating fact first reaches the
lock first, because it started earlier — but a consumer stalled on a database connection can lose
that race, and then the follow-up holds the lock, finds nothing, and throws. That is precisely the
case the retry catches: it releases, the beginning takes the lock, and the next attempt succeeds.
Neither mechanism alone is the guarantee. Together they are, within a process, and the specs say
"within a process" every time.

## Risks / Trade-offs

**A projection that never throws the new exception gains nothing from the retry.** → It gains the
lock, which removes the ordinary race on its own, and the guide says which exception to throw and
where. The retry exists for the residual case, and a projection that does not report it keeps the
behaviour it had.

**A bundle spanning many aggregates holds many locks.** → It holds them for one apply, in a fixed
order, and releases in reverse. The bundle that spans a hundred aggregates is a bundle from a command
that touched a hundred aggregates, and serialising it against each of them is the right thing.

**Throughput drops for a workload dominated by one hot aggregate.** → That workload was applying
those bundles in a racing order before; now it applies them one at a time, which is what it needed.
Bundles about other aggregates are unaffected, and an operator who wants the old behaviour has a
knob that did not exist before.

**Three seconds of retry on a bundle whose beginning never comes.** → Bounded, released between
attempts, and logged on every attempt with the stream and event type. The bundle then fails exactly
as it fails today, one log line richer.

**A type moves out of `Stratara.Mediator`.** → It was internal there, so no consumer could have
referenced it, and the mediator's behaviour does not change: it takes the same lock from the same
pool, now from a package it already referenced. `MediatorCommandWorkerTests` is the evidence that
nothing moved but the file.

**The `sagas` and `projections` guarantees are per process and the wording could be read as global.**
→ Every new requirement says "within a process" in its text, and the `outbox-and-messaging`
modification says explicitly that cross-consumer order is not promised. A host that needs global
order has the knob and the sentence that tells it so.

## Migration Plan

1. Write the worker tests that decide this change: same-aggregate bundles interleave today; a
   bundle that reports a missing prerequisite is discarded today. Both fail, the second to compile.
2. Move the lock pool to `Stratara.Abstractions`, retire the mediator's copy and its mirrored
   count, and add the exception beside `ConcurrencyException`.
3. Add the named policy and register it.
4. Change the two workers: option, lock, retry, log line.
5. Prove it with the counterpart tests: serialised, retried, released between attempts, configurable.
6. Document where a handler throws it and what the option does.

*Rollback:* revert. Nothing persists, no message changes shape, no stored state is written that a
previous version could not read, and a consumer that never threw the new exception never depended
on it.
