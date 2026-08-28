# DI Extensions Cheatsheet

> **Derived page.** The behaviour described here is specified by the `host-composition` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

The full menu of `Add*Services()` extensions Stratara exposes, by package.

## Umbrella extensions (`IHostApplicationBuilder`)

These wire entire worker / host concerns in one call. **Pick one per host.**

| Extension | Brings | Use for |
|---|---|---|
| `builder.AddBackendServices()` | Mediator, Identity, Session, Security, Resilience | ASP.NET API hosts |
| `builder.AddCommandWorkerServices()` | Common framework + command-handling worker (interactive lane) | Worker hosts that consume the `command` topic |
| `builder.AddHeavyCommandWorkerServices(dop?)` | Common framework + dedicated heavy-command worker | Worker hosts that drain long-running `IHeavyCommand` commands on a separate lane, so they don't starve interactive commands |
| `builder.AddEventProjectionWorkerServices()` | Common framework + projection worker | Worker hosts that update read-models |
| `builder.AddSagaWorkerServices()` | Common framework + saga worker | Worker hosts that orchestrate processes |
| `builder.AddEventStreamHashWorkerServices()` | Common framework + event-stream-hash worker | Worker hosts that hash event streams for tamper-evidence |
| `builder.AddOutboxWorkerServices()` | Common framework + outbox-drain worker | Worker hosts that publish from `outbox_entry` to the bus |

`AddCommonFrameworkServices()` is called transitively by every worker / backend extension above — you don't call it yourself.

## Domain registration (`IServiceCollection`)

These tell Stratara *what* to dispatch / project / saga. Call once per assembly that contains the relevant types.

| Extension | Discovers | Side-effect |
|---|---|---|
| `services.AddCommandHandlersFromAssemblyContaining<T>()` | `ICommandHandler<TCmd>` + `IQueryHandler<TCmd, TResult>` (the unified contract) | Per-handler `AddScoped` |
| `services.AddQueryHandlersFromAssemblyContaining<T>()` | `IQueryHandler<TQuery, TResult>` | Per-handler `AddScoped` |
| `services.AddProjectionsFromAssemblyContaining<T>()` | `IProjection` impls + their `HandleAsync(SomeEvent)` overloads | Per-projection `AddSingleton<IProjection>` + event-allowlist registration |
| `services.AddSagasFromAssemblyContaining<T>()` | `ISaga` impls + their `HandleAsync(SomeEvent)` overloads | Per-saga `AddSingleton<ISaga>` + event-allowlist registration |
| `services.AddAggregatesFromAssemblyContaining<T>()` | `IAggregate` impls + their `Apply(SomeEvent)` methods | Adds each aggregate **and** each apply-target event type to `ITrustedTypeResolver` |
| `services.AddDomainEventTypesFromAssemblyContaining<T>()` | The `Apply(SomeEvent)` parameter types of the assembly's aggregates | Adds **only** those event types to `ITrustedTypeResolver` — no aggregate types, no handler classes. For event-only hosts (projection/saga workers) that must deserialize bus/stream payloads without wiring handler dependencies |

## Security + integrity

| Extension | What it does |
|---|---|
| `services.AddStrataraFileKeyStore(configuration)` | Registers the production file-backed `EnvelopeFileKeyStore` (KEK-wrapped, versioned per-`KeyScope` DEKs) + `FileMasterKeyProvider` + the AES-GCM `ISecureBlobEncryptor`. Lives in `Stratara.Security` (dependency-light). Call **before** `AddSecurity()` so it wins the `TryAdd` race. |
| `services.AddSecurity()` | Wires `ISecureJsonSerializer` (`[EncryptData]`), the AES-GCM blob encryptor, and a **Development-only** `DummyKeyStore` fallback (`TryAdd`, so a real `IKeyStore` registered first wins). Adds the `KeyStoreStartupProbe` fail-fast guard. |
| `services.AddBusEnvelopeIntegrity(opts)` | Opt-in HMAC signing of `CommandEnvelope` + `EventBundle` |

## Validation

| Extension | What it does |
|---|---|
| `services.AddStrataraValidation()` | Registers the validation pipeline behavior. Call **before** other `AddPipelineBehavior*` so it runs outermost. |
| `services.AddValidatorsFromAssemblyContaining<T>()` | Discovers + registers every concrete `IValidator<T>` in the marker's assembly as scoped. |

## Tenant isolation

| Extension | What it does |
|---|---|
| `services.AddStrataraTenantIsolation()` | Registers the tenant-isolation pipeline behavior. Acts only on requests implementing `ITenantScopedRequest`; rejects a request whose `TenantId` ≠ the session's data-owner tenant with `TenantAccessDeniedException` (→ HTTP 403). Call **after** `AddStrataraValidation()`. |
| `services.AddStrataraTenantIsolation(o => o.Mode = TenantIsolationMode.Strict)` | Strict mode — additionally routes every cross-tenant operation (actor tenant ≠ data-owner tenant) through `ICrossTenantAuthorizer`. The shipped default denies all; register your own `ICrossTenantAuthorizer` to grant the cross-tenant case (e.g. a platform admin). |

## Resilience

| Extension | What it does |
|---|---|
| `services.AddResiliencePipelines()` | Registers the four Polly named pipelines — `ResilienceNames.MessageBus`, `.CommandDispatcher`, `.EventBundleDispatcher`, `.ConcurrencyConflict` |
| `services.AddStrataraResilienceBehavior()` | Mediator behavior that dispatches `IResilientRequest` through its chosen pipeline |

Use the `ResilienceNames` constants rather than the literal pipeline strings.

## Outbox transport (pick one per host)

| Extension | Bus |
|---|---|
| `builder.AddMessaging()` | RabbitMQ — extends `IHostApplicationBuilder`, and is what the worker composites call |
| `services.AddAzureServiceBus(connectionString)` | Azure Service Bus (connection-string) |
| `services.AddAzureServiceBusWithManagedIdentity(...)` | Azure Service Bus (DefaultAzureCredential) |

**One transport per host — the explicit one wins.** `AddMessaging()` registers `IMessageBus` for
RabbitMQ; the Azure Service Bus extensions *replace* it, so an explicit `AddAzureServiceBus` takes
effect even after a worker composite wired the RabbitMQ umbrella. Order no longer decides the
transport, but registering both in one host is still a smell — pick one.

## Write store + database contexts (`Stratara.EventSourcing.EntityFrameworkCore`)

| Extension | What it does |
|---|---|
| `services.AddNpgsqlWriteDbContextFactory<TContext>()` | Npgsql-backed `IDbContextFactory<TContext>` for the write-store context, plus the default `IWriteUnitOfWork` if none is registered |
| `services.AddNpgsqlReadDbContextFactory<TContext>()` | The same for a read-store context |
| `services.AddNpgsqlIdentityDbContextFactory<TContext>()` | The same for an identity-store context, **plus** a scoped resolution of the context itself so ASP.NET Identity can inject it directly |
| `services.AddWriteStore(configuration)` | Binds `EventSourcingOptions` from the `EventSourcing` section — snapshot cadence, batch sizes and the other write-side knobs |
| `services.AddCommandAuditing()` | `CommandAuditBehavior` for both command shapes — persists an audit row per dispatched command; queries pass through (`Stratara.EventSourcing.Pipeline.CommandAudit`) |

## Outbox coordination + projection replay (`Stratara.Outbox.RabbitMQ`)

| Extension | What it does |
|---|---|
| `services.AddRedisOutboxLock()` | Replaces the no-op `NullOutboxLock` with the Redis-backed one, which is what makes **more than one outbox-worker replica** safe. Needs an `IConnectionMultiplexer` — `AddCaching()` from `Stratara.Infrastructure` registers one. Lease it via `OutboxOptions.LockLeaseSeconds` |
| `services.AddProjectionReplayState()` | Registers the Redis-backed projection-replay state **and** `ProjectionReplayOptions` with its defaults, so the replay marking is leased (`LeaseSeconds`, default 300) rather than outliving a crashed replay. Idempotent |

## Health checks

| Extension | What it does |
|---|---|
| `builder.AddDefaultHealthChecks()` | Baseline `self` check, tagged `live` — surfaces on both `/health` and `/alive` once `MapDefaultEndpoints()` is called |
| `healthChecks.AddEventStoreHealthCheck(...)` | Verifies the write-side database is reachable. Needs the write store registered |
| `healthChecks.AddOutboxHealthCheck(...)` | Reports depth and age of the outbox backlog, degrading above a pending-entry threshold you pass in |

## Identity directory (`Stratara.Identity.EntityFrameworkCore`)

`TContext` is any `DbContext` whose model includes the directory tables — derive from
`IdentityDirectoryDbContext<TContext>` or call `modelBuilder.ApplyIdentityDirectoryModel()` in your
own `OnModelCreating`.

| Extension | What it does |
|---|---|
| `services.AddTenantMembershipStore<TContext>()` | EF `ITenantMembershipStore` (`tenant_membership`, `active_tenant`) — shares the request's context |
| `services.AddTenantMembershipStoreFromContextFactory<TContext>()` | Same store, a fresh context per operation (needs `AddDbContextFactory<TContext>()`) |
| `services.AddMembershipAuthorization()` | `IAuthorizationProvider` over tenant-scoped membership roles |
| `services.AddMembershipAuthorization<TUser>()` | Above ∪ global ASP.NET Identity roles |
| `services.AddMembershipCrossTenantAuthorizer(opts?)` | `ICrossTenantAuthorizer` for strict tenant isolation (membership OR a configured platform role) |
| `services.AddPermissionCatalog(c => …)` | Declares the permission vocabulary + role grants (throws on an undeclared grant) |
| `services.AddCatalogPermissionResolver()` | `IPermissionResolver` — membership roles through the catalog |
| `services.AddCatalogPermissionResolver<TUser>()` | Above ∪ global ASP.NET Identity roles |
| `services.AddSettingCatalog(c => …)` | Declares the setting vocabulary (defaults, `IsInherited`, `IsEncrypted`) |
| `services.AddSettingStore<TContext>()` | EF `ISettingStore` (`setting_entry`) **+** the `ISettingProvider` fallback facade |
| `services.AddSettingStoreFromContextFactory<TContext>()` | Same pair, a fresh context per operation |
| `services.AddApiKeyStore<TContext>()` | EF `IApiKeyStore` (`api_key`) — issue / import / validate / revoke / sweep |
| `services.AddApiKeyStoreFromContextFactory<TContext>()` | Same store, a fresh context per operation |

The `…FromContextFactory` variants exist because the plain registrations share one context across
every directory store in a request: a database context serves one operation at a time, and a store's
commit also commits whatever you have left unsaved on that context. A context per operation removes
both, and in exchange a store write no longer joins a transaction you opened on your own scoped
context. Calling both variants for the same store leaves whichever ran first in place. See
[Tenant Membership](../guides/tenant-membership.md).

`[RequirePermission]` is only enforced when the host also registers an authorizing mediator
(`services.AddAuthorizingMediator<MembershipAuthorizationProvider>()`). Without it — or without an
`IPermissionResolver` — the mediator's startup validator throws rather than let a guarded request
through unchecked.

## ASP.NET specific

| Extension | What it does |
|---|---|
| `builder.AddAspNetIdentity<TUser, TIdentityDbContext>()` | Channel-agnostic ASP.NET Core identity wiring (password/schema-v3/passkey defaults — **no lockout**) |
| `builder.AddAspNetIdentityWithSignInManager<TUser, TIdentityDbContext>()` | Above + **lockout defaults** + `IStrataraSignInManager` wrapper + localization |
| `builder.AddDevelopmentNoOpEmailSender<TUser>()` | Stub `IEmailSender` for Development — throws on every other environment name, including Staging (3.4.0; Production-only before) |
| `services.AddMembershipTenantClaim<TUser>()` | Stamps `stratara:tenant_id` into every issued principal (claims-factory decorator) |
| `services.AddMembershipTenantClaimsTransformation()` | Resolves `stratara:tenant_id` live per request — a tenant switch applies without re-issuing the sign-in |
| `services.AddStrataraPermissionPolicies()` | Turns every catalog permission into an on-demand policy → `[Authorize("sims.read")]` |
| `services.AddStrataraExternalLoginProvisioning<TUser>(opts?)` | JIT create/link of the local account on first external sign-in (fail-closed) |
| `app.MapDefaultEndpoints()` | `/health` + `/alive` endpoints (`Stratara.ServiceDefaults.AspNetCore`) |

### Authentication schemes (`AuthenticationBuilder`)

| Extension | Scheme |
|---|---|
| `.AddStrataraOpenIdConnect(configuration)` | Interactive external login, from `Identity:OpenIdConnect` |
| `.AddStrataraJwtBearer(configuration)` | API access tokens, from `Identity:JwtBearer` (multi-issuer by `iss`) |
| `.AddStrataraApiKey(opts?)` | `StrataraApiKey` — `X-Api-Key` header (opt-in query parameter) |
| `.AddStrataraAuthSchemeSelector(opts?)` | Policy scheme routing by request shape: API key → Bearer → cookie |

The `builder.Add*` identity rows are extension members of `IHostApplicationBuilder` in the
`Microsoft.Extensions.Hosting` namespace (Microsoft convention since v3.0.15). The `services.Add*`
rows and the authentication-scheme extensions above live in `Microsoft.Extensions.DependencyInjection`.
