# Stratara.Resilience

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

> **License:** [MIT](../../LICENSE).

Polly-based named resilience pipelines pre-wired for Stratara's messaging + outbox dispatch paths.

## Quick start

```csharp
services.AddResiliencePipelines();

// Resolve a specific named pipeline at the call site:
var pipeline = sp.GetRequiredService<ResiliencePipelineProvider<string>>()
    .GetPipeline(ResilienceNames.CommandDispatcher);

await pipeline.ExecuteAsync(async ct => {
    await DoFlakyWorkAsync(ct);
}, cancellationToken);
```

## Named pipelines

| Name | Constant | Strategy |
|---|---|---|
| Message bus | `ResilienceNames.MessageBus` | Exponential retry up to `int.MaxValue`, 10s → 60s, jitter, **then a circuit breaker** — opens after 10 consecutive failures and stays open 60s before a trial call |
| Command dispatcher | `ResilienceNames.CommandDispatcher` | 3 retries, exponential, 200ms, jitter |
| Event bundle dispatcher | `ResilienceNames.EventBundleDispatcher` | 3 retries, exponential, 200ms, jitter |
| Concurrency conflict | `ResilienceNames.ConcurrencyConflict` | Retry **only** on `ConcurrencyConflictException`, 5 attempts, short exponential, jitter |

The `ResilienceFactory` that builds these is `internal` — interact via DI and `ResilienceNames` only.

## Mediator resilience behavior

Hook a named pipeline into the in-process mediator pipeline per request — no manual `ExecuteAsync` plumbing:

```csharp
services.AddResiliencePipelines();
services.AddStrataraResilienceBehavior();   // after AddStrataraValidation() / AddStrataraTenantIsolation()

public sealed record ReserveStock(Guid Id, int Qty) : ICommand<bool>, IResilientRequest
{
    public string ResiliencePipelineName => ResilienceNames.ConcurrencyConflict;
}
```

Any request implementing `Stratara.Abstractions.Resilience.IResilientRequest` is dispatched inside the
named pipeline it selects; unmarked requests pass straight through. **Only mark handlers that are safe
to re-run** — naturally idempotent work, or operations guarded by optimistic concurrency (point at the
`ConcurrencyConflict` pipeline). Register the behavior *after* the validation / tenant-isolation
behaviors so a retry re-runs the handler, not the guards.

## Dependencies

- `Stratara.Abstractions` — for `IPipelineBehavior`, `IResilientRequest`, and `ConcurrencyConflictException`.
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Resilience` (Polly).
