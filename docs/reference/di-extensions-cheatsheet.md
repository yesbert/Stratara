# DI Extensions Cheatsheet

The full menu of `Add*Services()` extensions Stratara exposes, by package.

## Umbrella extensions (`IHostApplicationBuilder`)

These wire entire worker / host concerns in one call. **Pick one per host.**

| Extension | Brings | Use for |
|---|---|---|
| `builder.AddBackendServices()` | Mediator, Identity, Session, Security, Resilience | ASP.NET API hosts |
| `builder.AddCommandWorkerServices()` | Common framework + command-handling worker | Worker hosts that consume the `stratara.commands` topic |
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
| `services.AddAggregatesFromAssemblyContaining<T>()` | `IAggregate` impls + their `Apply(SomeEvent)` methods | Adds each aggregate + each apply-target type to `ITrustedTypeResolver` |
| `services.AddDomainEventTypesFromAssemblyContaining<T>()` | Types matching event marker conventions | Adds them to `ITrustedTypeResolver` |

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

## Resilience

| Extension | What it does |
|---|---|
| `services.AddResiliencePipelines()` | Registers Polly named pipelines (`Stratara.OutboxPublish`, `Stratara.HandlerRetry`, …) |

## Outbox transport (pick one per host)

| Extension | Bus |
|---|---|
| `services.AddRabbitMqBus(opts)` | RabbitMQ |
| `services.AddAzureServiceBus(opts)` | Azure Service Bus (connection-string) |
| `services.AddAzureServiceBusWithManagedIdentity(opts)` | Azure Service Bus (DefaultAzureCredential) |

## ASP.NET specific

| Extension | What it does |
|---|---|
| `builder.AddAspNetIdentity()` | Channel-agnostic ASP.NET Core identity wiring |
| `builder.AddAspNetIdentityWithSignInManager()` | Above + `SignInManager<TUser>` wrapper |
| `builder.AddDevelopmentNoOpEmailSender()` | Stub `IEmailSender` for development |
| `app.MapStrataraDefaults()` | Health endpoints + OpenAPI |

These three are extension members of `IHostApplicationBuilder` and live in the `Microsoft.Extensions.Hosting` namespace (Microsoft convention since v3.0.15).
