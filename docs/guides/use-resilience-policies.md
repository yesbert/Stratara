# Use Resilience Policies

> **Derived page.** The behaviour described here is specified by the `resilience` capability under
> `openspec/specs/`. That specification is the source; this page explains and illustrates it. Where
> the two disagree, the specification is right and this page is a bug.

## Read this before you opt in

**A retry re-runs your handler from the beginning.** Not a cached result, not a resumed
continuation — the whole handler, again. Everything it did on the failed attempt that was not rolled
back has already happened, and will happen a second time.

So a request may declare itself resilient only where re-running its handler is safe. In practice
that means one of two things:

- the handler is **idempotent** — running it twice leaves the same state as running it once, or
- the handler is **guarded by optimistic concurrency** — a second run against changed state loses
  the race and fails rather than double-applying.

If neither is true, do not opt in. The failure mode is a duplicated side effect: invisible on the
success path, irreversible on the failure path. A handler that charges a card, sends an email or
increments a counter is exactly the wrong candidate.

## Opting in

Resilience is per request, not per host. A request names the policy it wants:

```csharp
public sealed record ReserveSeat(Guid ShowId, Guid SeatId)
    : ICommand, IResilientRequest
{
    public string ResiliencePipelineName => ResilienceNames.ConcurrencyConflict;
}
```

```csharp
services.AddResiliencePipelines();          // the built-in named policies
services.AddStrataraValidation();
services.AddStrataraTenantIsolation();
services.AddStrataraResilienceBehavior();   // AFTER the guards — see below
```

A request that does not implement the marker is dispatched directly. No policy is resolved, nothing
is wrapped, and there is no cost to having the behaviour registered.

## The built-in policies

| `ResilienceNames` | Behaviour | Use it when |
|---|---|---|
| `ConcurrencyConflict` | Retries **only** `ConcurrencyConflictException`, up to six attempts, short exponential backoff | Your handler re-reads and re-applies on a version clash |
| `CommandDispatcher` | Up to four attempts, exponential backoff | Bounded retry around command dispatch |
| `EventBundleDispatcher` | Up to four attempts, exponential backoff | Bounded retry around bundle dispatch |
| `MessageBus` | Retries indefinitely, behind a circuit breaker | Broker traffic, where dropping the message is worse than waiting |

`ConcurrencyConflict` is the one most in-process handlers want, and it is deliberately narrow:
anything that is not a concurrency conflict propagates on the first attempt. A retry policy that
swallowed every exception class would turn a permanent failure into a slow one.

`MessageBus` never gives up on purpose — a transient broker outage must not drop messages, and the
outbox has already persisted the work. The circuit breaker exists to bound the duty cycle so a
permanently misconfigured broker surfaces in metrics roughly once per breaker cycle instead of as an
unbounded retry storm.

## Register the behaviour after the guards

`AddStrataraResilienceBehavior()` goes **after** `AddStrataraValidation()` and
`AddStrataraTenantIsolation()`. The resilience stage then sits inside the guard stages, so a retry
re-runs the handler and not the guards.

That ordering is not cosmetic. Re-running validation is wasted work; re-running tenant isolation and
authorization is worse, because permissions can be re-resolved mid-retry and produce a different
decision than the one the request was admitted under. The request would then complete under an
authorization answer it never passed.

Calling it more than once installs the behaviour once, so a request is not retried twice over.

## See also

- [Write a Command Handler](write-a-command-handler.md) — the handler a retry re-runs.
- [Write a Validator](write-a-validator.md) — the guard stage the retry sits inside.
- [Enforce Tenant Isolation](enforce-tenant-isolation.md) — the other guard stage.
