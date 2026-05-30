# Glossary

How Stratara uses common CQRS / Event-Sourcing terms. These definitions are operative — they shape how the framework behaves, not just how we talk.

## Mediator

The in-process dispatcher (`IMediator`) that routes a `ICommand`/`ICommand<T>`/`IQuery<T>` to its handler. Resolved from DI per scope. Wrapped in `AuthorizingMediator` when `[RequireRole]` semantics are active.

## Command vs Query

| | `ICommand` | `ICommand<T>` | `IQuery<T>` |
|---|---|---|---|
| Has side effects | ✅ | ✅ | ❌ (read-only) |
| Returns a result | ❌ | ✅ | ✅ |
| Default routing | via outbox (`ICommandOutboxDispatcher`) | via `IMediator` (in-process) | via `IMediator` (in-process) |

See **[Routing Conventions](../reference/routing-conventions.md)** for the full table including edge cases (mutations against infrastructure, process-local signaling, etc.).

## Aggregate

A consistency boundary in your domain. Stratara provides two marker interfaces in `Stratara.Abstractions`:

- `IAggregate` — base, has `Guid Id`.
- `ITenantAggregate : IAggregate` — tenant-scoped, has `Guid TenantId`.

Aggregates are **rebuilt from events** by `IAggregationService.AggregateAsync<T>(streamId, fromVersion, toVersion)`. Aggregate properties use **public `set`** (not `private set`) so snapshot JSON deserialization works.

## Event

An immutable `sealed record` representing one atomic business fact (`AccountOpened`, `AmountDeposited`, …). Persisted to the event stream; consumers see them as `IEvent` or `IEvent<TPayload>`. Never remove a persisted enum value — mark `[Obsolete]` only.

## Event Source

The `IEventSource` interface: append events to a stream + reconstruct aggregates via `IAggregationService`. The default implementation persists to `event_stream_entry`/`snapshot`/`command_log_entry`/`outbox_entry` tables via `Stratara.EventSourcing.EntityFrameworkCore`.

## Projection

A read-model builder driven by event bundles from the bus. Implements `IProjection` and a `HandleAsync(IEnumerable<IEvent>, …)` method. Stratara discovers and registers projections via `AddProjectionsFromAssemblyContaining<T>()`; they run inside the `EventProjectionWorker`.

## Saga (Process Manager)

A long-running process that reacts to events by issuing more commands. Implements `ISaga`. Stratara routes events through `AddSagasFromAssemblyContaining<T>()` + the `SagaOrchestrationWorker`.

## Validation

Request validation as a mediator pipeline behavior. An `IValidator<T>` (from `Stratara.Abstractions.Validation`) runs before the handler; only `ValidationSeverity.Error` failures block (the pipeline throws `StrataraValidationException`), while `Warning`/`Info` pass through and are logged. Wired with `AddStrataraValidation()` + `AddValidatorsFromAssemblyContaining<T>()`.

## Key Scope

The addressing unit for data-encryption keys (`KeyScope` in `Stratara.Abstractions.Security`): a `DataSensitivityLevel` optionally narrowed to a tenant and/or user. The production `EnvelopeFileKeyStore` (from `Stratara.Security`) holds a KEK-wrapped, versioned key per scope; rotation keeps old ciphertext readable, while `RevokeAsync` / `EraseScopeAsync` crypto-shred for GDPR Art. 17.

## Outbox

The transactional outbox pattern. When a handler emits events, they land in the `outbox_entry` table inside the same DB transaction. The `OutboxWorker` polls + publishes to the bus (RabbitMQ / Azure Service Bus). At-least-once delivery + idempotent consumers.

## Bus Envelope

What actually travels on the wire. Stratara has two envelope shapes:

- `CommandEnvelope` — for async command dispatch.
- `EventBundle` — for fan-out to projections + sagas after a successful save.

Both can carry an opt-in **HMAC signature** via `IBusEnvelopeSigner` (see [HMAC Bus-Envelope](../guides/hmac-bus-envelope.md)).

## Actor vs Subject

The Stratara session model distinguishes:

- **Actor** — *who triggered* the operation. Used for audit + rate-limit keying + event payload's `CreatedByUserId`. Fields: `ActorTenantId`, `ActorUserId`.
- **Subject** — *the data owner*. Used for routing + encryption AAD + query filter. Fields: unprefixed `TenantId`, `UserId?`.
- **ClientId** — neither actor nor subject; the *connection identity* (browser tab / phone call / SSR session). Used as `Conversation` / `ProactiveSession` aggregate stream-id + SignalR target.

## Trusted Type Resolver

`ITrustedTypeResolver` — the allowlist of types Stratara will deserialize from the bus. Registered via `AddAggregatesFromAssemblyContaining<T>()` (which auto-scans `Apply(…)` methods) + `AddDomainEventTypesFromAssemblyContaining<T>()`. The `EncryptionMetadataDriftGuard` checks the allowlist at host-start.

## EncryptData

`[EncryptData]` — attribute that marks a property for AES-GCM encryption at serialization time. Tenant-aware **Additional Authenticated Data (AAD)** binds the ciphertext to the tenant — a leaked key in tenant A's ciphertext can't be replayed against tenant B's record.

## LoggerMessage Source-Gen

Stratara mandates `[LoggerMessage]` for all new logging. Per-package extension files live under `Diagnostics/Extensions/Logger*Extensions.cs`. Event-ID schema centralised in `Stratara.Diagnostics/LogEvents.cs` (`100_000` framework range; consumer apps start at `200_000+`).
