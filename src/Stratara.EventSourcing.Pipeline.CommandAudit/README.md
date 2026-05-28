# Stratara.EventSourcing.Pipeline.CommandAudit

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Mediator pipeline behavior that records an audit row for every dispatched command in the Stratara event-sourced stack. Both arities are provided so consumers can register a single behavior pair and have it apply to all command shapes.

## What's in the box

| Type | Purpose |
|---|---|
| `CommandAuditBehavior<TRequest>` | Runs the audit-write step before delegating to `next()` for `IRequest` (commands without result). |
| `CommandAuditBehavior<TRequest, TResult>` | Same, for `IRequest<TResult>` (commands with result + queries). Only records when the request also implements `ICommandBase` — queries flow through untouched. |
| `CommandAuditWriter` (internal) | Opens a transaction on `IWriteUnitOfWork`, writes via `ICommandAuditRepository.AddAsync`, commits. |

The behavior only audits commands — query requests reach the same generic interface but are filtered by the `is ICommandBase` check, so registering both behaviors application-wide is safe.

## Quick start

```csharp
// At composition time, alongside the other framework pipeline behaviors:
builder.Services
    .AddPipelineBehaviorWithResult(typeof(CommandAuditBehavior<,>))
    .AddPipelineBehavior(typeof(CommandAuditBehavior<>));
```

The behaviors resolve `IWriteUnitOfWork` from DI (ships with `Stratara.EventSourcing.EntityFrameworkCore` in the default deployment). No additional registration is needed.

## Dependencies

- `Stratara.Abstractions` — for `IPipelineBehavior<,>`, `IRequest`/`IRequest<T>`, `ICommandBase`, `IWriteUnitOfWork`, `ICommandAuditRepository`.
- `JetBrains.Annotations` — `[UsedImplicitly]` on the public behavior classes (DI-instantiated, no static call site).

At runtime an `IWriteUnitOfWork` implementation must be registered — typically by referencing `Stratara.EventSourcing.EntityFrameworkCore` and calling `AddWriteStore(IConfiguration)`.

## Security note — command payload contents

The audit row stores `CommandTypeName` and the serialized `CommandJson` of the dispatched command. **Whatever fields your commands carry land in the audit table.** Sensitive data (passwords, tokens, API keys, encryption material) MUST NOT live on a command record — or must be marked with `[EncryptData]` so the registered `ISecureJsonSerializer` encrypts them before persistence.

The default Stratara registration uses `ISecureJsonSerializer` (AES-GCM + tenant-scoped AAD) for the audit serialization, so `[EncryptData]`-annotated properties are protected. Plain-text properties go to disk unencrypted — treat the audit table accordingly when designing command shapes.
