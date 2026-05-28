# Stratara.ServiceDefaults.AspNetCore

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

ASP.NET Core extensions on top of `Stratara.ServiceDefaults`. Reference from every API host.

## What's in the box

| Extension | Purpose |
|---|---|
| `AddDefaultHealthChecks` | Registers a `self`-tagged health check that always reports Healthy + tags it as `live` so the `/alive` predicate can filter on it. |
| `MapDefaultEndpoints` | Maps `/health` (full health-check report) and `/alive` (only the `live`-tagged checks). |
| `ConfigureAspNetOpenTelemetry` | Wires ASP.NET Core request instrumentation on metrics + tracing; filters out the health-check + aliveness endpoints from traces so they don't pollute the request stream. |

## Quick start

```csharp
builder.ConfigureOpenTelemetry();                  // from Stratara.ServiceDefaults
builder.ConfigureAspNetOpenTelemetry();            // from this package
builder.AddDefaultHealthChecks();
builder.ConfigureSerilog();                        // from Stratara.ServiceDefaults

var app = builder.Build();
app.MapDefaultEndpoints();
```

## Dependencies

- `Stratara.ServiceDefaults` — base `ConfigureOpenTelemetry` and shared endpoint constants.
- `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Instrumentation.AspNetCore`.
