# Stratara.Abstractions

> **License:** [MIT](../../LICENSE).

Contract interfaces and wire-level POCO records for the Stratara framework. Library-safe — depends only on `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, and `Stratara.Contracts`. No EF Core or message-bus runtime.

Use this when you need to reference Stratara types without pulling in any concrete implementation (Mediator runtime, EF Core, RabbitMQ, etc.). Typical consumers: handler/projection libraries that ship without a host.

## Contents

- `Stratara.Abstractions.Mediator` — `IMediator`, `IRequest`, `IRequest<T>`, `ICommand`, `ICommand<T>`, `IQuery<T>`, `ICommandHandler<T>`, `IQueryHandler<T,R>`, `IPipelineBehavior<T>`, `IPipelineBehavior<T,R>`, `IAggregateScopedCommand`.
- `Stratara.Abstractions.EventSourcing` — `IAggregationService`, `IEventSource`, `IEventStreamRepository`, `ISnapshotRepository`, `IEvent`, `IEvent<T>`, `IAggregateCreationEvent`, `IChangeSetHandler`, `EventChainAnchor`, `EventSubject`, `ConcurrencyException`. Plus wire-types: `EventStreamEntry`, `Snapshot`.
- `Stratara.Abstractions.Persistence` — `IUnitOfWork`, `IWriteUnitOfWork`, `IReadUnitOfWork`, `ITransaction`, `IDbResolver`.
- `Stratara.Abstractions.Outbox` — `ICommandOutboxDispatcher`, `IEventBundleOutboxDispatcher`, `IOutboxRepository`. Plus wire-type `OutboxEntry`.
- `Stratara.Abstractions.Messaging` — `IMessageBus`, `IMessagingIdentifier`, `IEventBusConsumer`, `IEventBusPublisher`.
- `Stratara.Abstractions.Session` — `ISessionContextProvider`.
- `Stratara.Abstractions.Multitenancy` — `ITenantService`, `ICurrentUserService`, `ITenantScopedRequest`, `ICrossTenantAuthorizer`, `TenantAccessDeniedException`, `ITenantMembershipStore`. Plus wire-types: `TenantMembership`, `MembershipStatus`.
- `Stratara.Abstractions.Projections` — `IProjectionReplayState`.
- `Stratara.Abstractions.Security` — `IEncryptionFactory`, `IKeyStore`, `IMasterKeyProvider`, `ISecureBlobEncryptor`, `ISecureJsonSerializer`. Plus wire-types: `KeyScope`, `KeyMaterial`, `EncryptedData`, `DataSensitivityLevel`, `EncryptDataAttribute`.
- `Stratara.Abstractions.Validation` — `IValidator<T>`, `ValidationResult`, `ValidationFailure`, `ValidationSeverity`, `StrataraValidationException`.
- `Stratara.Abstractions.Entities` — `IEntity`, `IBucket`, `IHasRowVersion`, `IMultiTenant`, `ITenantEntity`, `IUserIdentity`.
- `Stratara.Abstractions.BackgroundTasks` — `IBackgroundTaskQueue`. Plus wire-types: `BackgroundTaskInfo`, `BackgroundTaskStatus`.
- `Stratara.Abstractions.Commands` — `IUpdateCommand`.
- `Stratara.Abstractions.Authorization` — `RequireRoleAttribute`, `RequirePermissionAttribute`, `IAuthorizationProvider`, `IPermissionResolver`, `PermissionCatalog`, `AuthorizationException`, `PermissionAuthorizationException`.
- `Stratara.Abstractions.Settings` — `ISettingProvider`, `ISettingStore`, `SettingCatalog`. Plus wire-types: `SettingDefinition`, `SettingScope`.
- `Stratara.Abstractions.ApiKeys` — `IApiKeyStore`. Plus wire-types: `ApiKeyDescriptor`, `ApiKeyIssueRequest`, `IssuedApiKey`.

## Why split

NuGet consumers without an event-sourcing host can adopt Stratara's CQRS contracts and authorization model without dragging in EF Core, RabbitMQ, or the WriteStore.
