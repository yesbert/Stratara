# Stratara

**Stratara** is an application-agnostic **CQRS / Event Sourcing / Mediator** framework for **.NET 10**, distributed as a NuGet family of 20 lockstep-versioned packages.

It provides the wiring that production event-sourced apps tend to write from scratch: an in-process mediator with pipeline behaviors, an outbox-pattern dispatcher with RabbitMQ and Azure Service Bus implementations, a saga runtime, a projection runtime, EF Core write / read / identity stores on PostgreSQL, OpenTelemetry-and-Serilog defaults, channel-agnostic identity primitives, Polly-backed resilience, and source-generated logging — all opt-in à la carte.

> **License:** Stratara ships under **FSL-1.1-MIT** ([Functional Source License 1.1 with MIT Future License](LICENSE)). Source-available, **not OSI-approved OSS** — see [License](#license) before adoption.

## Documentation

Full docs, conceptual overview, getting-started walkthrough, guides, samples and the auto-generated API reference live at **[docs.stratara.tech](https://docs.stratara.tech)**.

## Install

```bash
# Minimum: in-process mediator + pipeline behaviors
dotnet add package Stratara.Mediator

# Event-sourced apps with the full stack
dotnet add package Stratara.EventSourcing.EntityFrameworkCore
dotnet add package Stratara.EventSourcing.WorkerDefaults
dotnet add package Stratara.Outbox.RabbitMQ
```

Hello-mediator in five lines:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMediator();
builder.Services.AddCommandHandlersFromAssemblyContaining<Program>();
var app = builder.Build();
await app.Services.GetRequiredService<IMediator>().HandleAsync(new MyCommand(), CancellationToken.None);
```

## Package map

Lockstep-versioned NuGet family — every package in the table below ships at the same `<VersionPrefix>`, bumped together (Microsoft.Extensions.* convention).

| Tier | Package | Purpose |
|---|---|---|
| A | `Stratara.Abstractions` | Contract interfaces + POCO records (no implementation) |
| A | `Stratara.Contracts` | Wire-level POCO contracts |
| A | `Stratara.Diagnostics` | `ActivitySource` / `Meter` / log-event-ID schema |
| A | `Stratara.Resilience` | Polly named pipelines |
| A | `Stratara.Sessions` | Actor / Subject session model + ASP.NET middleware |
| B | `Stratara.Mediator` | In-process mediator + pipeline behaviors |
| B | `Stratara.Domain` | Tenant aggregate + lifecycle events |
| B | `Stratara.Shared` | Umbrella re-export of A/B abstractions + source-generated logger extensions |
| B | `Stratara.ServiceDefaults` | OpenTelemetry + Serilog defaults |
| C | `Stratara.EventSourcing.EntityFrameworkCore` | Write / read / identity stores on PostgreSQL |
| C | `Stratara.EventSourcing.Pipeline.CommandAudit` | Command-audit pipeline behavior |
| C | `Stratara.EventSourcing.WorkerDefaults` | Worker-host wiring composites |
| C | `Stratara.Projections` | Projection runtime |
| C | `Stratara.Sagas` | Saga runtime |
| C | `Stratara.Outbox.RabbitMQ` | Outbox + RabbitMQ-backed `IMessageBus` |
| C | `Stratara.Outbox.AzureServiceBus` | Outbox + Azure Service Bus-backed `IMessageBus` |
| C | `Stratara.Infrastructure` | Cross-cutting infrastructure glue |
| C | `Stratara.Identity.Core` | Channel-agnostic identity primitives |
| C | `Stratara.Identity.AspNetCore` | Channel-agnostic ASP.NET Core identity wiring (sign-in manager wrapper + i18n + email-sender stub) |
| C | `Stratara.ServiceDefaults.AspNetCore` | ASP.NET health checks + request OpenTelemetry |

**Tier order**: a Tier-N package may only reference Tier-(≤N). Tier-A has no inbound dependencies from B or C.

## Quick start

The fastest path is the **learning samples** under [`samples/`](samples/) — each is self-contained, shares the same bank-account / money-transfer domain, and reads top-to-bottom in 5–20 minutes.

| # | Sample | Concept |
|---|---|---|
| 1 | [`Stratara.Sample.CqrsBasics`](samples/Stratara.Sample.CqrsBasics) | `IMediator` + `ICommand` / `IQuery` + handler discovery |
| 2 | [`Stratara.Sample.EventSourced`](samples/Stratara.Sample.EventSourced) | Event-sourced aggregate + read-side projection |
| 3 | [`Stratara.Sample.OutboxWorker`](samples/Stratara.Sample.OutboxWorker) | Outbox + message bus + background worker |
| 4 | [`Stratara.Sample.MoneyTransferSaga`](samples/Stratara.Sample.MoneyTransferSaga) | Saga / process manager |
| 5 | [`Stratara.Sample.AspNetCoreApi`](samples/Stratara.Sample.AspNetCoreApi) | HTTP minimal-API → mediator wiring |

```bash
dotnet run --project samples/Stratara.Sample.CqrsBasics
```

Each sample also has a step-by-step walkthrough under [docs.stratara.tech/samples](https://docs.stratara.tech/samples/).

## Build from source

Requires the **.NET 10 SDK** — `global.json` pins the version; `dotnet --version` should report `10.0.x`.

```bash
# Build the publish solution filter (every packable csproj + tests)
dotnet build Stratara.Publish.slnf -c Release

# Run the test suite (xUnit v3 with Microsoft Testing Platform)
dotnet test
```

## Versioning

Lockstep across the whole family — one `<VersionPrefix>` in `Directory.Build.props` controls every package. Tag-driven builds (`v*`) publish stable versions; main-branch pushes publish `{VersionPrefix}-preview.{BuildId}` pre-releases. SemVer applies — see [`CHANGELOG.md`](CHANGELOG.md) for per-release notes.

## License

**FSL-1.1-MIT** — [Functional Source License 1.1 with MIT Future License](LICENSE).

You may use Stratara for any purpose other than building a directly competing product. After **two years**, each released version converts to plain **MIT** under the FSL "Grant of Future License" clause. The full MIT future-license text is included in the [`LICENSE`](LICENSE) file.

## Contributing

Stratara's GitHub repository is a one-way mirror of an internal Azure DevOps source-of-truth, force-pushed as a single squashed commit per release. **We do not currently accept pull requests** — any PR opened against the mirror would be lost on the next sync.

What we welcome:

- **Bug reports** — [open an issue](https://github.com/yesbert/Stratara/issues/new/choose) with the bug template.
- **Questions** — open an issue with the question template (check [docs.stratara.tech](https://docs.stratara.tech) first).
- **Security issues** — see [`SECURITY.md`](SECURITY.md), please do not file a public issue.

Full details on the contribution model: [`CONTRIBUTING.md`](CONTRIBUTING.md). Community standards: [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md). Getting help: [`SUPPORT.md`](SUPPORT.md).
