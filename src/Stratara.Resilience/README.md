# Stratara.Resilience

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

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
| Message bus | `ResilienceNames.MessageBus` | Exponential retry up to `int.MaxValue`, 10s → 60s, jitter |
| Command dispatcher | `ResilienceNames.CommandDispatcher` | 3 retries, exponential, 200ms, jitter |
| Event bundle dispatcher | `ResilienceNames.EventBundleDispatcher` | 3 retries, exponential, 200ms, jitter |

The `ResilienceFactory` that builds these is `internal` — interact via DI and `ResilienceNames` only.

## Dependencies

- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Resilience` (Polly).
