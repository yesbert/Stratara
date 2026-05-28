# Stratara.Infrastructure

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Cross-cutting infrastructure plumbing for the Stratara framework — the Tier-C glue that lets downstream apps wire authorization, DI composition, and worker-stack configuration with a single reference.

## Contents

- Authorization decorators over command-outbox dispatch (`AuthorizingCommandOutboxDispatcher`).
- DI composition helpers that wire Mediator, Outbox, Identity, and EFCore into a hosted app.
- Configuration providers and option binders used by the worker stack.

## Dependencies

Transitively depends on `Stratara.Contracts`, `Stratara.EventSourcing.EntityFrameworkCore`, `Stratara.Mediator`, `Stratara.Outbox.RabbitMQ`, `Stratara.Sessions`, `Stratara.Shared`.

## Behavioural notes

### `AddSecurity()` — IKeyStore registration (since 3.0.11)

`AddSecurity()` registers Stratara's security stack including the `IKeyStore` abstraction. The default is a `TryAddSingleton<IKeyStore, DummyKeyStore>` fallback — but `DummyKeyStore` since 3.0.11 throws `InvalidOperationException` in **any environment other than `Development`** (whitelist guard to prevent production data exposure from the demo encryption key). Hosts on `Staging`, `QA`, `UAT`, `Preview`, or any custom environment **must** register a real `IKeyStore` implementation before calling `AddSecurity()`:

```csharp
// Recommended composition root
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSecurity();   // DummyKeyStore fallback is fine
}
else
{
    builder.Services.AddSingleton<IKeyStore, AzureKeyVaultKeyStore>();  // or AwsKmsKeyStore, HsmKeyStore, ...
    builder.Services.AddSecurity();
}
```

`KeyStoreStartupProbe` logs a `Warning` (event id `LogEvents.KeyManagement.DummyKeyStoreActive` = `112_001`) at host start when the resolved `IKeyStore` is `DummyKeyStore` — even in Development — so an accidental dependency on the dummy is loud rather than silent.

**Why the change:** Before 3.0.11 the guard only blocked `IsProduction()`. Hosts in any other environment silently encrypted with the world-known constant pass-phrase `"StrataraTestKey"` baked into the shipping NuGet — a Staging or QA copy of production data could be decrypted by anyone reading the source. The whitelist guard makes this configuration crash-fast at host build instead of allowing silent data exposure.

### `AddCaching()` — Redis registration

`AddCaching()` used to delegate to `builder.AddRedisClient("redis")` from `Aspire.StackExchange.Redis`. After the Aspire-wrapper removal it registers `IConnectionMultiplexer` directly via `ConnectionMultiplexer.Connect(...)` from `StackExchange.Redis`. **The method signature is unchanged**, but the Aspire-only side-effects are gone:

- **No automatic Redis health check.** Add one explicitly with `AddHealthChecks().AddRedis(connectionString)` (from `AspNetCore.HealthChecks.Redis`) if your host exposes `/health` and you want Redis covered.
- **No automatic OpenTelemetry Redis instrumentation.** Add `OpenTelemetry.Instrumentation.StackExchangeRedis` and `.AddRedisInstrumentation()` to your `TracerProviderBuilder` if you want Redis spans in your traces.

The connection-string lookup (`ConnectionStrings:redis` in configuration) is identical to the pre-cleanup behaviour.
