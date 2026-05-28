# Stratara.Abstractions

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Contract interfaces and wire-level POCO records for the Stratara framework. Library-safe — depends only on `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, and `Stratara.Contracts`. No EF Core or message-bus runtime.

Use this when you need to reference Stratara types without pulling in any concrete implementation (Mediator runtime, EF Core, RabbitMQ, etc.). Typical consumers: handler/projection libraries that ship without a host.

## Contents

- `Stratara.Abstractions.Mediator` — `IMediator`, `IRequest`, `IRequest<T>`, `ICommand`, `ICommand<T>`, `IQuery<T>`, `ICommandHandler<T>`, `IQueryHandler<T,R>`, `IPipelineBehavior<T>`, `IPipelineBehavior<T,R>`, `IAggregateScopedCommand`.
- `Stratara.Abstractions.EventSourcing` — `IAggregationService`, `IEventSource`, `IEventStreamRepository`, `ISnapshotRepository`, `IEvent`, `IEvent<T>`, `IAggregateCreationEvent`, `IChangeSetHandler`, `EventChainAnchor`, `EventSubject`, `ConcurrencyException`. Plus wire-types: `EventStreamEntry`, `Snapshot`.
- `Stratara.Abstractions.Persistence` — `IUnitOfWork`, `IWriteUnitOfWork`, `IReadUnitOfWork`, `ITransaction`, `IDbResolver`.
- `Stratara.Abstractions.Outbox` — `ICommandOutboxDispatcher`, `IEventBundleOutboxDispatcher`, `IOutboxRepository`. Plus wire-type `OutboxEntry`.
- `Stratara.Abstractions.Messaging` — `IMessageBus`, `IMessagingIdentifier`, `IEventBusConsumer`, `IEventBusPublisher`.
- `Stratara.Abstractions.Session` — `ISessionContextProvider`.
- `Stratara.Abstractions.Multitenancy` — `ITenantService`, `ICurrentUserService`.
- `Stratara.Abstractions.Projections` — `IProjectionReplayState`.
- `Stratara.Abstractions.Security` — `IEncryptionFactory`, `IKeyStore`, `ISecureBlobEncryptor`, `ISecureJsonSerializer`. Plus wire-types: `EncryptedData`, `DataSensitivityLevel`, `EncryptDataAttribute`.
- `Stratara.Abstractions.Entities` — `IEntity`, `IBucket`, `IHasRowVersion`, `IMultiTenant`, `ITenantEntity`, `IUserIdentity`.
- `Stratara.Abstractions.BackgroundTasks` — `IBackgroundTaskQueue`. Plus wire-types: `BackgroundTaskInfo`, `BackgroundTaskStatus`.
- `Stratara.Abstractions.Commands` — `IUpdateCommand`.
- `Stratara.Abstractions.Authorization` — `RequireRoleAttribute`, `IAuthorizationProvider`, `AuthorizationException`.

## Why split

NuGet consumers without an event-sourcing host can adopt Stratara's CQRS contracts and authorization model without dragging in EF Core, RabbitMQ, or the WriteStore.
