# Stratara.Infrastructure

> **License:** [MIT](../../LICENSE).

Cross-cutting infrastructure plumbing for the Stratara framework — the Tier-C glue that lets downstream apps wire authorization, DI composition, and worker-stack configuration with a single reference.

## Contents

- Authorization decorators over command-outbox dispatch (`AuthorizingCommandOutboxDispatcher`) — enforces `[RequireRole]` and `[RequirePermission]` on the command's runtime type before it reaches the outbox.
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
    // The production key store Stratara ships (Stratara.Security): KEK-wrapped, versioned
    // per-scope DEKs. Register it before AddSecurity() so it wins the TryAdd race.
    builder.Services.AddStrataraFileKeyStore(builder.Configuration);
    builder.Services.AddSecurity();
}
```

Stratara ships one production `IKeyStore` — `EnvelopeFileKeyStore`, via `AddStrataraFileKeyStore`.
There is no built-in Azure Key Vault / AWS KMS / HSM store; if you need one, implement `IKeyStore`
against your provider and register it in place of the file store.

`KeyStoreStartupProbe` logs a `Warning` (event id `LogEvents.KeyManagement.DummyKeyStoreActive` = `112_001`) at host start when the resolved `IKeyStore` is `DummyKeyStore` — even in Development — so an accidental dependency on the dummy is loud rather than silent.

**Why the change:** Before 3.0.11 the guard only blocked `IsProduction()`. Hosts in any other environment silently encrypted with the world-known constant pass-phrase `"StrataraTestKey"` baked into the shipping NuGet — a Staging or QA copy of production data could be decrypted by anyone reading the source. The whitelist guard makes this configuration crash-fast at host build instead of allowing silent data exposure.

### `AddCaching()` — Redis registration

`AddCaching()` used to delegate to `builder.AddRedisClient("redis")` from `Aspire.StackExchange.Redis`. After the Aspire-wrapper removal it registers `IConnectionMultiplexer` directly via `ConnectionMultiplexer.Connect(...)` from `StackExchange.Redis`. **The method signature is unchanged**, but the Aspire-only side-effects are gone:

- **No automatic Redis health check.** Add one explicitly with `AddHealthChecks().AddRedis(connectionString)` (from `AspNetCore.HealthChecks.Redis`) if your host exposes `/health` and you want Redis covered.
- **No automatic OpenTelemetry Redis instrumentation.** Add `OpenTelemetry.Instrumentation.StackExchangeRedis` and `.AddRedisInstrumentation()` to your `TracerProviderBuilder` if you want Redis spans in your traces.

The connection-string lookup (`ConnectionStrings:redis` in configuration) is identical to the pre-cleanup behaviour.
