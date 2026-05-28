# Stratara.Diagnostics

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Generic observability primitives shared by all Stratara packages. Use this to emit `Activity` / `Meter` instruments under a uniform source name and stable event-ID schema.

## Contents

- `ApplicationDiagnostics` — `ActivitySource("Stratara.Application")` + `Meter("Stratara.Service")` + tag-name constants (`correlation.id`, `causation.id`, `tenant.id`, `user.id`) + metric names (`event_source.append.conflicts`). These names are part of the public observability contract — renaming them breaks downstream Grafana/Tempo queries.
- `LogEvents` — `[LoggerMessage]` event-ID ranges per domain (ChangeSet=100_000s, BackgroundTasks=101_000s, EventStore=102_000s, …, Messaging=108_000s, Update=109_000s, Saga=110_000s). Even hundreds = info/debug, `_1xx` = error.
- `LoggerScopeExtensions.BeginCreateAggregateScope` / `BeginUpdateAggregateScope` — pre-baked logging scopes for the create/update aggregate flows.

## Quick reference

```csharp
using var activity = ApplicationDiagnostics.Activity.Source
    .StartActivity("CreateOrder");
activity?.SetTag(ApplicationDiagnostics.TagNames.TenantId, tenantId);

ApplicationDiagnostics.Metrics.EventSourceAppendConflicts.Add(1,
    new("aggregate.type", "Order"));
```

## Dependencies

- `Microsoft.Extensions.Logging.Abstractions`
- `OpenTelemetry.Api`
