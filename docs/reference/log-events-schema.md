# LogEvents Schema

Stratara mandates source-generated `[LoggerMessage]` for all new logging — no `logger.LogInformation(...)` direct calls. Every log event has a stable `EventId` from a known range.

## ID range allocation

| Range | Owner |
|---|---|
| `0 – 99_999` | Reserved (Microsoft / framework defaults) |
| `100_000 – 199_999` | **Stratara framework** (this repo) |
| `200_000+` | Consumer applications |

Sub-buckets inside the framework's `100_000` range are defined in `src/Stratara.Diagnostics/LogEvents.cs`. The current allocation:

| Bucket | Subsystem | LogEvents nested class |
|---|---|---|
| `110_000s` | EventBundle / Outbox dispatch | `LogEvents.EventBundle`, `LogEvents.Outbox` |
| `111_000s` | Bus-Envelope integrity (HMAC) | `LogEvents.BusEnvelopeIntegrity` |
| `113_000s` | BusEnvelope startup probes | `LogEvents.BusEnvelopeStartupProbe` |
| `120_000s` | Projection runtime | `LogEvents.Projection`, `LogEvents.ChangeSet` |
| `130_000s` | Saga runtime | `LogEvents.Saga` |
| `140_000s` | Identity + auth | `LogEvents.Identity`, `LogEvents.Authorization` |
| `150_000s` | Security + encryption | `LogEvents.KeyStore`, `LogEvents.Encryption` |
| `160_000s` | Background tasks + workers | `LogEvents.BackgroundTask`, `LogEvents.Worker` |
| `170_000s` | Event-stream hashing | `LogEvents.EventStreamHashing` |

Consult `src/Stratara.Diagnostics/LogEvents.cs` for the authoritative current list — buckets shift as features mature.

## Authoring a new log event

1. **Pick a bucket** in `LogEvents.cs`. If your subsystem doesn't have one yet, add the nested class with a `_baseId` constant.
2. **Add the constant**:
   ```csharp
   public static class Projection
   {
       private const int _baseId = 120_000;
       public const int ProjectionStarted = _baseId + 1;
       public const int EventsNotRelevantForProjection = _baseId + 2;
       // …
   }
   ```
3. **Add the `[LoggerMessage]` partial method** in the appropriate `Diagnostics/Extensions/Logger*Extensions.cs`:
   ```csharp
   [LoggerMessage(
       EventId = LogEvents.Projection.ProjectionStarted,
       Level = LogLevel.Information,
       Message = "Projection {ProjectionName} started.")]
   public static partial void LogProjectionStarted(this ILogger logger, string projectionName);
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
