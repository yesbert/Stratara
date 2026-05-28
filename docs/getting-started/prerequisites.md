# Prerequisites

## Required

- **.NET SDK 10.0** or newer. Check via `dotnet --version`. Stratara targets `net10.0` exclusively.

That's it for the minimum surface. The `Stratara.Mediator` + `Stratara.Abstractions` combo runs in-process with no infrastructure.

## Optional (per package)

| If you use… | You'll need… |
|---|---|
| `Stratara.EventSourcing.EntityFrameworkCore` | PostgreSQL 15+ (the EF Core provider; SQLite-only is not supported as the dialect uses Postgres-specific features) |
| `Stratara.Outbox.RabbitMQ` | RabbitMQ 3.13+ with the management plugin (publisher confirms + reconnect) |
| `Stratara.Outbox.AzureServiceBus` | An Azure Service Bus namespace; either a connection string or a `DefaultAzureCredential`-resolvable managed identity |
| `Stratara.ServiceDefaults` | OpenTelemetry Collector endpoint (OTLP) if you want traces/metrics exported; runs no-op otherwise |
| `Stratara.Identity.AspNetCore` | An ASP.NET Core host; no other identity provider locked in |

## Recommended local tooling

- `docker` — for spinning up Postgres + RabbitMQ via the integration-test compose file.
- An IDE with C# 14 support (Stratara uses `extension(…)` member syntax in places).

## How to verify

```bash
dotnet --list-sdks         # 10.0.x present
dotnet new console -o ping
cd ping
dotnet add package Stratara.Mediator
dotnet add package Stratara.Abstractions
dotnet build               # should restore + build clean
```

If `dotnet build` is green, the SDK + NuGet feed are healthy. Move on to **[First Stratara App](first-stratara-app.md)**.
