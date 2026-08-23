# Stratara.ServiceDefaults

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

> **License:** [MIT](../../LICENSE).

Cross-host observability defaults for the Stratara stack. Reference from every host (API, worker) to get OpenTelemetry + Serilog wired up with sensible defaults.

## What's in the box

| Extension | Purpose |
|---|---|
| `ConfigureOpenTelemetry` | Logging + metrics + tracing with HTTP, EF Core, RabbitMQ, runtime instrumentation; OTLP exporter wired up automatically when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Accepts optional `MeterProviderBuilder` / `TracerProviderBuilder` callbacks for host-specific extras. |
| `ConfigureSerilog` | Structured logging with destructuring attributes, async console sink, OTLP sink (gRPC or HTTP/Protobuf based on `OTEL_EXPORTER_OTLP_PROTOCOL`), dev-mode log cleanup at `{Path.GetTempPath()}/stratara-logs/{service-name}.log` — the OS temp directory, so not literally `/tmp` on Windows or macOS. |
| `ConfigureSerilogBootstrapLogger` | Sets up `Log.Logger` as a bootstrap logger before the host is built, so early-startup errors surface to the console. |

## Quick start

```csharp
builder.ConfigureOpenTelemetry();
builder.ConfigureSerilog();
```

## It deletes your development log file on start-up

In the Development environment only, configuring Serilog **deletes** the current log file before
opening it: `%TEMP%/stratara-logs/{OTEL_SERVICE_NAME}.log`, where the service name falls back to
`Unknown` when `OTEL_SERVICE_NAME` is unset. The intent is a clean file per debugging session rather
than one that grows across dozens of restarts.

Two consequences worth knowing before they cost you an hour:

- **A crash you wanted to read is gone the moment you restart to reproduce it.** Copy the file
  before restarting, or point the sink elsewhere for that session.
- **Two development services with no `OTEL_SERVICE_NAME` share `Unknown.log` and delete each
  other's.** Set the variable per service.

Outside Development nothing is deleted.

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
