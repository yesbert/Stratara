> **Status:** approved

# Let no fact overtake its beginning

## Why

The projection worker and the saga worker each open as many subscriptions as the host has
processors, all on one queue, and each subscription attaches its own consumer. The broker deals
bundles round-robin across them, so two bundles published milliseconds apart — the fact that creates
an entity and the first fact about it — land on two consumers and run concurrently. Whichever finishes
first wins, and nothing relates that to the order in which they were published.

The command side does not have this problem. Its worker takes a per-aggregate lock before it runs a
command, so two commands on one aggregate never execute at the same time in one process
(`mediator-dispatch` → *Commands scoped to one aggregate are serialised against each other*). The
projection and saga workers were built with the same parallelism and without the lock.

This is observed, not theorised. In a consumer's end-to-end run:

```
18:56:44.197  the fact that creates knowledge entry 01a05e54… is stored
18:56:44.247  a saga reacts and dispatches the next command for it
18:56:44.289  the projection receives "processing step started" for 01a05e54…
              — and has no row for it, because the creating fact is on another consumer
```

What happens next is decided by the projection, and both of its choices lose. A projection that logs
a warning and continues acknowledges a bundle it never applied — the read model is silently missing a
fact, with nothing recording which one. A projection that throws — which is what the `projections`
capability tells it to do, and what that consumer now does — has its bundle rejected without
redelivery: the transport requeues only a concurrency conflict and discards everything else. Either
way the fact is lost to the view, and the only in-band signal that a fact is missing — "I have never
seen this entity begin" — is ambiguous, because it also fires when the beginning is fifty milliseconds
away.

The event store is intact in every case. What is lost is the read model, and it stays lost until a
full replay rebuilds it.

**Why now:** a consumer has just turned that warning into a failure, because it was the only way to
notice a *genuinely* missing fact (the one `lose-no-fact-to-a-late-subscription` closed). Under
unordered parallel consumption that signal now discards bundles that would have succeeded on the next
attempt. The consumer cannot fix this on its side: the parallelism is not configurable, the
serialisation is not available to it, and there is no way for a handler to say "not yet" that the
transport distinguishes from "failed".

## What Changes

- **Bundles about one aggregate are applied one at a time within a worker process.** The projection
  worker and the saga worker take the same per-aggregate lock the command worker already takes,
  keyed on the stream the bundle's events belong to. Two bundles on one aggregate serialise; bundles
  on different aggregates keep running in parallel. This removes the race in the ordinary case: the
  consumer that received the creating fact first almost always takes the lock first, and the
  follow-up waits behind it.
- **A handler can say "the fact this refers to has not been applied yet".** A new exception type in
  the published abstractions, alongside the existing concurrency exception, means exactly that. The
  worker retries the bundle a bounded number of times in-process with a short backoff, releasing the
  aggregate lock between attempts so the creating fact can land. Only after the retries are exhausted
  does the bundle fail as any unhandled failure does. A consumer that never throws it sees no change.
- **The degree of parallelism is configurable** for the projection worker and the saga worker, in
  their existing options, with the same fallback the heavy-command lane already has: a value that is
  not a positive number means the processor count. A host that needs strict order can run one
  consumer; a host that does not keeps today's default.
- **The absence of cross-consumer ordering is stated.** The delivery guarantee says at-least-once
  and says nothing about order. It now says that order across consumers is not promised, so a
  projection written the obvious way — assuming the beginning arrives before the follow-up — is
  written against a guarantee that exists, namely the in-process serialisation above, rather than
  against an assumption.

Nothing about the queue topology, the wire format or the transports changes. No published member is
removed or renamed; the change is additive and lands in a minor version.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `projections`: added — bundles about one aggregate are applied one at a time within a process; a
  projection can report a missing prerequisite and is retried briefly before the bundle fails; the
  worker's degree of parallelism is configurable. The requirement *A failing projection stops the
  bundle* is unchanged: exhausting the retries is that failure.
- `sagas`: added — the same three guarantees for the saga worker. The requirement *Sagas run in
  parallel with each other, and in order within themselves* is unchanged; it speaks about sagas
  within one bundle, and the new requirement speaks about bundles across consumers.
- `outbox-and-messaging`: modified — *Delivery is at least once, never at most once* now also states
  that delivery order across consumers is not guaranteed, and points at the per-process serialisation
  as the guarantee a handler may rely on instead.
- `resilience`: added — a named policy that retries only a missing prerequisite, a bounded number of
  times, with a short backoff. It joins the named policies the framework registers, so a consumer can
  reference or override it like the others.

## Impact

**Changed**

`src/Stratara.Projections/Services/ProjectionWorker.cs` and `src/Stratara.Sagas/Services/SagaWorker.cs`
(per-aggregate lock, retry, configurable parallelism); `src/Stratara.Projections/Services/ProjectionOptions.cs`
and `src/Stratara.Sagas/Services/SagaOptions.cs` (the new option); `src/Stratara.Resilience/Resilience/ResilienceNames.cs`,
`ResilienceFactory.cs` and `DependencyInjection/ResilienceServiceCollectionExtensions.cs` (the new named
policy); `src/Stratara.Diagnostics/LogEvents.cs` and the logger extensions (the retry is logged).

**Added**

The per-key lock pool the command worker already uses moves from `Stratara.Mediator`, where it is
internal, to `Stratara.Abstractions` as a public type, next to the bucket marker that already lives
there. The mediator's copy and its duplicated bucket count are deleted; the count is stated once, in
the pool, and the constant in `Stratara.Shared` refers to it. `BucketCalculator` and `BucketConstants`
stay where they are, because moving a public type is a namespace break. The new exception type in
`Stratara.Abstractions`, alongside `ConcurrencyException`.

**Consumer impact**

A consumer that changes nothing gets the serialisation and the default parallelism it had. A consumer
whose projections look up the entity a fact refers to can throw the new exception where it currently
warns or throws something else, and gets the retry. A consumer that implements `IMessageBus` itself
is unaffected: the retry runs inside the worker before the transport sees an outcome, so no transport
has to learn a new meaning for a nack.

**Out of scope, deliberately**

Broker-side bounded redelivery with a delivery counter. The transport declares classic queues, which
have no counter; a beginning that arrives minutes late — its consumer crashed and its bundle went
back to the queue — is what replay is for, not what a retry is for. Any change to queue topology,
including per-stream queues or consistent-hash routing, which would give real cross-process order at
the price of a broker-specific design the second transport cannot follow.

**Origin**

A consumer's framework finding, discovered 2026-09-01 in its end-to-end suite and captured in its own
findings log. Analysed and fixed here; the consumer adopts it through a version bump.

**Unaffected**

Every other capability, the outbox drain, the dispatch path, the replay, and the wire format. No
message changes shape.
