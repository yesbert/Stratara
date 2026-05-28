# Stratara.ServiceDefaults

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Cross-host observability + service-discovery defaults for the Stratara stack. Reference from every host (API, worker) to get OpenTelemetry + Serilog wired up with sensible defaults.

## What's in the box

| Extension | Purpose |
|---|---|
| `ConfigureOpenTelemetry` | Logging + metrics + tracing with HTTP, EF Core, RabbitMQ, runtime instrumentation; OTLP exporter wired up automatically when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Accepts optional `MeterProviderBuilder` / `TracerProviderBuilder` callbacks for host-specific extras. |
| `ConfigureSerilog` | Structured logging with destructuring attributes, async console sink, OTLP sink (gRPC or HTTP/Protobuf based on `OTEL_EXPORTER_OTLP_PROTOCOL`), dev-mode log cleanup at `/tmp/stratara-logs/{service-name}.log`. |
| `ConfigureSerilogBootstrapLogger` | Sets up `Log.Logger` as a bootstrap logger before the host is built, so early-startup errors surface to the console. |

## Quick start

```csharp
builder.ConfigureOpenTelemetry();
builder.ConfigureSerilog();
```

## Sibling packages

- **`Stratara.EventSourcing.WorkerDefaults`** — one-stop `AddBackendServices` / `AddXxxWorkerServices` composites that wire the framework's mediator + outbox + projections + sagas stack.
- **`Stratara.ServiceDefaults.AspNetCore`** — ASP.NET-specific extras: `AddDefaultHealthChecks` + `MapDefaultEndpoints` (`/health`, `/alive`) and ASP.NET request OTel instrumentation.

## Dependencies

- `Stratara.Shared` — diagnostics base (`ApplicationDiagnostics.Activity.SourceName`).
- OpenTelemetry runtime + exporter packages.
- Serilog sinks (Console, File, Async, OpenTelemetry).
- `Destructurama.Attributed` for destructuring conventions.
- `Microsoft.Extensions.Http.Resilience` + `Microsoft.Extensions.ServiceDiscovery`.

> **Prerelease dependencies.** This package transitively pulls in two prerelease OpenTelemetry instrumentation packages that have no stable release yet: `OpenTelemetry.Instrumentation.EntityFrameworkCore` (beta) and `RabbitMQ.Client.OpenTelemetry` (RC). Both ride the stable OpenTelemetry 1.15.x core. NU5104 is suppressed in this csproj with that justification — consumers inherit the prerelease deps transitively. We will swap to GA as soon as the vendors ship stable.
