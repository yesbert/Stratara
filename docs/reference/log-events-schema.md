---
title: "LogEvents Schema"
description: "The stable EventId ranges Stratara logs under, who owns each range, and the source-generated LoggerMessage rule that keeps the ids from drifting."
---

# LogEvents Schema

> **Derived page.** The behaviour described here is specified by the `observability` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Stratara mandates source-generated `[LoggerMessage]` for all new logging — no `logger.LogInformation(...)` direct calls. Every log event has a stable `EventId` from a known range.

## ID range allocation

| Range | Owner |
|---|---|
| `0 – 99_999` | Reserved (Microsoft / framework defaults) |
| `100_000 – 199_999` | **Stratara framework** (this repo) — currently allocated `100_000 – 117_999` |
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
| `114_000s` | Tenant isolation | `LogEvents.TenantIsolation` |
| `115_000s` | External-login provisioning | `LogEvents.ExternalLoginProvisioning` |
| `116_000s` | API keys | `LogEvents.ApiKeys` |
| `117_000s` | Orleans execution model | `LogEvents.Orleans` |

Even hundreds are info/debug, the `_1xx` band is error (e.g. `100_002` info, `100_101` error). Consult `src/Stratara.Diagnostics/LogEvents.cs` for the authoritative current list — buckets shift as features mature.

### The Orleans execution model (`117_000s`)

| Id | Constant | Level | When |
|---|---|---|---|
| `117_001` | StoreReaderStarted | Information | A store reader was activated for its consumer and partition |
| `117_002` | StoreReaderStopped | Information | A store reader was deactivated |
| `117_003` | CommandRecorded | Debug | A command was recorded before its dispatch returned |
| `117_004` | CommandResumed | Information | A recorded command whose hand-over lapsed was handed over again; carries the attempt |
| `117_005` | StoreReaderRetired | Information | A store reader of a partition beyond the host's partition count unregistered its keep-alive and reads nothing |
| `117_006` | ResumeHeldBackByReplay | Information | The drain holds recorded commands back while a full replay is active; logged once when it begins |
| `117_007` | ResumeReleasedAfterReplay | Information | The drain resumes recorded commands again after a full replay held them back |
| `117_008` | HandlerStoppedWithSilo | Information | A handler on a grain path was cancelled because its silo stopped; carries the handler and what it ran for |
| `117_101` | PartitionStalled | Warning | A store reader stopped at an entry it cannot apply; the checkpoint stays before it |
| `117_102` | EntryAttemptFailed | Warning | One attempt to apply an entry failed and is retried under the preceding-fact policy |
| `117_103` | CatchUpFaulted | Error | A read of the store failed; the partition counts as stalled and the next wake-up or poll reads again |
| `117_104` | CommandKept | Warning | A recorded command reached its delivery or conflict bound and was kept for an operator; carries both counts and the last failure |
| `117_105` | CompletionFlushFailed | Warning | Removing completed commands failed; the drain resumes them |
| `117_106` | PermitReleasedByExpiry | Warning | A heavy-work permit was released because its holder left or its lease lapsed |
| `117_107` | DirectoryCheckFailed | Error | No storage-backed grain directory is registered; the silo does not start |
| `117_108` | IntentRenewalFailed | Warning | Renewing a running command's hand-over failed; the next renewal tries again |
| `117_109` | PermitRenewalLost | Warning | A running heavy unit's permit was no longer held and is reclaimed |
| `117_110` | PermitReleaseFailed | Warning | Releasing a heavy unit's permit failed; its lease releases it |
| `117_111` | RecordedCommandsWithoutIntentStore | Warning | The drain found recorded commands on a silo without an intent store |
| `117_112` | IntentAttemptFailed | Warning | One attempt to run a recorded command failed; carries the command, its type and its aggregate |
| `117_113` | HandOverFailed | Warning | Handing a recorded command to its grain failed; the drain hands it over again after the grace |
| `117_114` | PermitReclaimRefused | Warning | A running heavy unit whose permit was lost was refused when it registered again; it runs outside the bound until a permit is free |
| `117_115` | IntentUnsignedResumed | Warning | A recorded command without a signature was resumed under `Permissive` |
| `117_116` | IntentIntegrityResumed | Warning | A recorded command whose signature does not verify was resumed under `Permissive` |
| `117_117` | IntentUnsignedKept | Error | A recorded command without a signature was kept for an operator under `Strict` |
| `117_118` | IntentIntegrityKept | Error | A recorded command whose signature does not verify was kept for an operator under `Strict` |
| `117_119` | SingletonWorkFailed | Error | A run of a singleton work threw; the work runs again at its next period |
| `117_120` | RolesUnpublished | Error | A silo registers roles or singleton work without publishing them — its directory was not registered with `AddStrataraOrleans` — and does not start |
| `117_121` | NudgeFailed | Debug | A commit's wake-up could not be delivered to a store reader; the reader's poll reads the commit instead |
| `117_122` | IntentRoutingRefused | Warning | A recorded command's stored routing disagrees with its signed envelope; it is kept under strict integrity mode and resumed by the signed claim otherwise |
| `117_123` | StoreReaderPauseLapsed | Warning | A store reader's pause lapsed because its pauser — a rebuild, a replay's reset or a test host's reset — stopped renewing it within the lease; carries the consumer, the partition, the pauser and how many pauses remain, and the reader resumes once none is left |
| `117_124` | IntentHandOverDropped | Debug | A hand-over found its command already taken by another runner — a second resumption of the same claim — or completed or kept, and was dropped without running the handler |

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
- `Stratara.Shared.Diagnostics.Extensions.ChangeSetFieldNames` (in the `Stratara.Projections` package) — wraps `IReadOnlyList<ChangeDetail>`.

## What never to do

- ❌ `logger.LogInformation("…", arg)` — direct logger calls.
- ❌ `if (logger.IsEnabled(LogLevel.Debug)) { logger.LogXxx(...) }` — manual IsEnabled guards. The source-gen formatter checks IsEnabled internally; expensive arguments belong in deferred-formatting wrappers.
- ❌ Sharing an `EventId` across two `[LoggerMessage]` methods — IDs are unique per code path.
- ❌ Repurposing a freed `EventId` — once shipped, an `EventId` is part of the schema's observable contract.
