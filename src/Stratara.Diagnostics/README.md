# Stratara.Diagnostics

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

> **License:** [MIT](../../LICENSE).

Generic observability primitives shared by all Stratara packages. Use this to emit `Activity` / `Meter` instruments under a uniform source name and stable event-ID schema.

## Contents

- `ApplicationDiagnostics` — `ActivitySource("Stratara.Application")` + `Meter("Stratara.Service")` + tag-name constants (`correlation.id`, `causation.id`, `tenant.id`, `user.id`, `event.type`, `request.type`, `outcome`, `outbox.kind`) + the metric instruments below. These names are part of the public observability contract — renaming them breaks downstream Grafana/Tempo queries.

### Metric instruments (`ApplicationDiagnostics.Metrics`)

| Instrument | Type | Tags |
|---|---|---|
| `event_source.append.conflicts` | counter | `aggregate.type` |
| `event_source.events.appended` | counter | `event.type`, `aggregate.type` |
| `outbox.published` | counter | `outbox.kind` (`command` / `event`) |
| `command.duration` (ms) | histogram | `request.type`, `outcome` |
| `projection.events.processed` | counter | `event.type`, `outcome` |
| `projection.bundle.duration` (ms) | histogram | `outcome` |
| `saga.events.processed` | counter | `event.type`, `outcome` |
| `saga.bundle.duration` (ms) | histogram | `outcome` |
| `saga.inflight` | up/down counter | — |

Projections and sagas are real-time bus subscribers without a persisted checkpoint, so these report **throughput and latency**, not consumer lag.

- `LogEvents` — `[LoggerMessage]` event-ID ranges per domain (ChangeSet=100_000s, BackgroundTasks=101_000s, EventStore=102_000s, …, Saga=110_000s, EventBundleIntegrity=111_000s, KeyManagement=112_000s, BusEnvelopeIntegrity=113_000s, TenantIsolation=114_000s, ExternalLoginProvisioning=115_000s, ApiKeys=116_000s). Even hundreds = info/debug, `_1xx` = error. Consumer applications start at 200_000.
- `LoggerScopeExtensions.BeginCreateAggregateScope` / `BeginUpdateAggregateScope` — pre-baked logging scopes for the create/update aggregate flows.

## Quick reference

```csharp
using var activity = ApplicationDiagnostics.Activity.Source
    .StartActivity("CreateOrder");
activity?.SetTag(ApplicationDiagnostics.TenantIdTagName, tenantId);

ApplicationDiagnostics.Metrics.EventSourceAppendConflicts.Add(1,
    new("aggregate.type", "Order"));
```

## Dependencies

- `Microsoft.Extensions.Logging.Abstractions`
- `OpenTelemetry.Api`
