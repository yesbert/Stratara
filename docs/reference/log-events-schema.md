# LogEvents Schema

> **Derived page.** The behaviour described here is specified by the `observability` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Stratara mandates source-generated `[LoggerMessage]` for all new logging — no `logger.LogInformation(...)` direct calls. Every log event has a stable `EventId` from a known range.

## ID range allocation

| Range | Owner |
|---|---|
| `0 – 99_999` | Reserved (Microsoft / framework defaults) |
| `100_000 – 199_999` | **Stratara framework** (this repo) — currently allocated `100_000 – 113_999` |
| `200_000+` | Consumer applications |

Sub-buckets inside the framework's `100_000` range are defined in `src/Stratara.Diagnostics/LogEvents.cs`. The current allocation:

| Bucket | Subsystem | LogEvents nested class |
|---|---|---|
| `100_000s` | Change-set / aggregate-update | `LogEvents.ChangeSet` |
| `101_000s` | Background-task queue | `LogEvents.BackgroundTasks` |
| `102_000s` | Event-store append / read | `LogEvents.EventStore` |
| `103_000s` | Validation | `LogEvents.Validation` |
| `104_000s` | Projection worker | `LogEvents.Projection` |
| `105_000s` | Command-handling worker | `LogEvents.CommandProcessing` |
| `106_000s` | Outbox worker | `LogEvents.OutboxProcessing` |
| `107_000s` | Event-stream-hash worker | `LogEvents.EventStreamHashing` |
| `108_000s` | Messaging | `LogEvents.Messaging` |
| `109_000s` | Aggregate update | `LogEvents.Update` |
| `110_000s` | Saga worker | `LogEvents.Saga` |
| `111_000s` | Event-bundle integrity | `LogEvents.EventBundleIntegrity` |
| `112_000s` | Key management | `LogEvents.KeyManagement` |
| `113_000s` | Bus-envelope integrity (startup probe) | `LogEvents.BusEnvelopeIntegrity` |

Even hundreds are info/debug, the `_1xx` band is error (e.g. `100_002` info, `100_101` error). Consult `src/Stratara.Diagnostics/LogEvents.cs` for the authoritative current list — buckets shift as features mature.

## Authoring a new log event

1. **Pick a bucket.** In your own app, start at `200_000+`; the `100_000` block is the framework's. Add a nested class per subsystem.
2. **Add the constants** as plain literals — even hundreds for info/debug, the `_1xx` band for errors:
   ```csharp
   public static class OrderProjection
   {
       public const int OrderProjectionStarted = 200_001;   // info
       public const int OrderProjectionFailed  = 200_101;   // error
   }
   ```
3. **Add the `[LoggerMessage]` partial method** in a `public static partial class` (the source generator requires it):
   ```csharp
   [LoggerMessage(
       EventId = OrderProjection.OrderProjectionStarted,
       Level = LogLevel.Information,
       Message = "Order projection {ProjectionName} started.")]
   public static partial void LogOrderProjectionStarted(this ILogger logger, string projectionName);
   ```

## Logger-extension file naming

| Convention | Example |
|---|---|
| One `Logger{Subject}Extensions.cs` per subsystem | `LoggerProjectionExtensions.cs`, `LoggerSagaExtensions.cs` |
| Namespace `Stratara.Shared.Diagnostics.Extensions` regardless of source package | All packages' logger extensions live in this single namespace |
| Class is `public static partial class` | Required by the LoggerMessage source generator |

## Parameter-type discipline

`[LoggerMessage]` source-gen accepts any type, but Stratara's Clean Code rule restricts parameters to **simple types** (`string`, `Guid`, `int`, `DateTimeOffset`, enums). For aggregate / collection arguments that would otherwise force expensive formatting at call-time, use a small wrapper struct with `ToString()` — the formatter calls `ToString()` lazily, only when the channel is enabled.

Canonical examples in the repo:

- `Stratara.Shared.Diagnostics.Extensions.DistinctEventTypeNames` — wraps `IReadOnlyList<IEvent>`.
- `Stratara.Projections.Diagnostics.Extensions.ChangeSetFieldNames` — wraps `IReadOnlyList<ChangeDetail>`.

## What never to do

- ❌ `logger.LogInformation("…", arg)` — direct logger calls.
- ❌ `if (logger.IsEnabled(LogLevel.Debug)) { logger.LogXxx(...) }` — manual IsEnabled guards. The source-gen formatter checks IsEnabled internally; expensive arguments belong in deferred-formatting wrappers.
- ❌ Sharing an `EventId` across two `[LoggerMessage]` methods — IDs are unique per code path.
- ❌ Repurposing a freed `EventId` — once shipped, an `EventId` is part of the schema's observable contract.
