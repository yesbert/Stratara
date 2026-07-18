# Stratara.Testing

Test doubles and assertion helpers for applications built on the Stratara framework. Unit-test your
event-sourced aggregates, encryption, messaging, and session-aware code without spinning up Postgres
or RabbitMQ testcontainers.

## Contents

- `AggregateTestHarness<T>` / `Aggregate.Rehydrate<T>(...)` — given/when/then rehydration of an
  aggregate from events, using the same `Apply(...)` dispatch as production. Throws on an unmapped
  event so a forgotten overload fails the test (opt out with `IgnoringUnmappedEvents()`).
- `InMemoryKeyStore` — an `IKeyStore` that mints random 256-bit DEKs per scope and supports
  rotation / revocation / scope-erasure, without a master KEK or key file.
- `TestBlobEncryptor.CreateAesGcm()` — the **real** AES-GCM `ISecureBlobEncryptor` over an
  `InMemoryKeyStore`, so blob round-trips exercise production encryption.
- `InMemoryMessageBus` — an `IMessageBus` with synchronous in-process dispatch and a `Published`
  list for assertions.
- `TestSessionContext` / `TestSessionContextProvider` — preset Actor/Subject `SessionContext`
  values and an `ISessionContextProvider` double.
- `InMemoryTenantMembershipStore` — an `ITenantMembershipStore` mirroring the EF store's contract
  semantics, including the membership-guarded active-tenant selection and the erasure sweeps.
- `InMemorySettingStore` — an `ISettingStore` with exact-scope reads/writes, so the scoped-settings
  fallback chain can be exercised without a database.
- `TestTenants.Of("acme")` — stable, deterministic tenant/user ids derived from readable slugs.
- `TestEvent.Create(payload, ...)` — wrap an event payload in `IEvent<T>` with realistic metadata.
- `ProjectionTester.HandleAsync(projection, event)` — invoke a projection's (private) `HandleAsync`
  handler directly, so you can unit-test it against mocked repositories.

## Example

```csharp
var account = AggregateTestHarness<Account>
    .Given(new AccountOpened(id, "Ada", 100m))
    .And(new AmountWithdrawn(id, 30m))
    .Build();

Assert.Equal(70m, account.Balance);
```

## Dependencies

- `Stratara.Abstractions`, `Stratara.Contracts`, `Stratara.Shared`, `Stratara.Security`
- `Microsoft.Extensions.DependencyInjection`

Reference it from your test projects only (`<PackageReference Include="Stratara.Testing" />`). It is
not meant for production code paths — the `InMemoryKeyStore` and `DummyKeyStore` provide no
durability or KEK custody.
