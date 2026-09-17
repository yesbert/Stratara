---
title: "Testing Patterns"
description: "The xUnit v3 and Microsoft Testing Platform conventions the framework and its consumers share, and the test-support packages that make them short."
---

# Testing Patterns

> **Derived page.** The behaviour described here is specified by the `test-support` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Stratara tests use **xUnit v3** on **Microsoft Testing Platform (MTP)**. The framework + consumer apps share the same conventions.

## Project layout

```
tests/
├── Stratara.{Package}.Tests/             — unit tests, fast, no Docker
├── Stratara.{Package}.IntegrationTests/  — Testcontainers (Postgres / RabbitMQ); skipped by local-gauntlet
├── Stratara.SmokeTests/                  — composition-/lifecycle tests, console-runner
└── Stratara.Samples.SmokeTests/          — runs each sample as a subprocess, asserts on stdout
```

The `*IntegrationTests` suffix is a CI boundary — `local-gauntlet.sh` and the build workflow skip them; the integration workflow runs them.

## Run a single project

```bash
dotnet test tests/Stratara.Shared.Tests
```

## Run the full local gauntlet

```bash
./scripts/local-gauntlet.sh
```

Builds the full repo (`Stratara.slnx`), runs every unit test project, then `dotnet pack`s every packable project as a sanity check.

## The `Stratara.Testing` package

`Stratara.Testing` ships test doubles and a rehydration harness so you can unit-test
Stratara-based code without a Postgres or RabbitMQ testcontainer. Reference it from test
projects only:

```xml
<PackageReference Include="Stratara.Testing" />
```

**Rehydrate an aggregate (given/when/then).** `AggregateTestHarness<T>` applies events through the
same `Apply(...)` dispatch as the production aggregation service. It throws if an event has no
matching `Apply` overload, so a forgotten overload fails the test:

<!-- stratara-snippet-ignore: narrative fragment - the stream id comes from the surrounding text -->
```csharp
var account = AggregateTestHarness<Account>
    .Given(new AccountOpened(id, "Ada", 100m))
    .And(new AmountWithdrawn(id, 30m))
    .Build();

Assert.Equal(70m, account.Balance);
// One-liner: Aggregate.Rehydrate<Account>(new AccountOpened(...), new AmountWithdrawn(...));
```

**Encryption without a key file.** `InMemoryKeyStore` is a full `IKeyStore` (rotation, revocation,
scope-erasure); `TestBlobEncryptor.CreateAesGcm()` builds the real AES-GCM encryptor over it:

```csharp
var keyStore = new InMemoryKeyStore();
var encryptor = TestBlobEncryptor.CreateAesGcm(keyStore);
```

**Messaging + session.** `InMemoryMessageBus` dispatches in-process and records every message in
`Published` for assertions; `TestSessionContextProvider.ForTenant(tenantId)` presets the ambient
Actor/Subject session; `TestTenants.Of("acme")` yields stable, readable tenant ids.

**Projections.** Projection handlers are private (the runtime finds them by reflection), so call them
in a test with `ProjectionTester.HandleAsync(projection, TestEvent.Create(new MyEvent(...)))` and
assert on your mocked repository / unit-of-work.

**Real event store.** For an end-to-end write-side test on the genuine `IEventSource` (no mocks, no
Postgres), add `Stratara.Testing.EntityFrameworkCore` and use `EventStoreTestHost` — it runs the
real stack on in-memory SQLite. See its package README.

### What the host lets you observe

`EventStoreTestHost` makes what happened visible without a broker:

- **`Outbox`** is a `RecordingEventBundleOutboxDispatcher`. Every event bundle a save produced lands in
  its `Bundles`, ready for assertions.
- **`Session`** is the `TestSessionContextProvider` the whole stack reads. It starts on
  `EventStoreTestHost.DefaultTenantId`. Change its context and every later `ExecuteAsync` and
  `AggregateAsync` runs under the new tenant.
- **`Services`** is the root service provider, for anything else you need to resolve.

```csharp
await using var host = EventStoreTestHost.Create(s => s.AddAggregatesFromAssemblyContaining<Account>());
var accountId = Guid.NewGuid();

await host.ExecuteAsync(async events =>
{
    await events.CreateAsync<Account>(accountId, new AccountOpened(accountId, "Ada", 100m));
    await events.SaveChangesAsync();
});

var published = host.Outbox.Bundles;           // the bundle that save produced
var actingTenant = host.Session.Current!.TenantId;   // EventStoreTestHost.DefaultTenantId

var otherTenant = Guid.NewGuid();
host.Session.Set(TestSessionContext.ForTenant(otherTenant));   // what follows runs as otherTenant
```

## On the Orleans execution model

To test handlers, projections, sagas and timers on the execution model, add `Stratara.Testing.Orleans` and use
`ExecutionModelTestHost`. It runs one silo in the test's process: reminders and the grain directory in memory, the
real write stack, commit-order reader, checkpoint store and intent store on in-memory SQLite, and every period the
model keeps as a reminder or a poll shortened to seconds. There is no cluster, broker or database server, and no
Docker. Register the roles with the calls production uses, so the test proves the production registration:

```csharp
await using var silo = await ExecutionModelTestHost.CreateAsync(services => services
    .AddAggregatesFromAssemblyContaining<IAppMarker>()
    .AddCommandHandlersFromAssemblyContaining<IAppMarker>()
    .AddProjectionsFromAssemblyContaining<IAppMarker>()
    .AddStrataraAggregateGrains()
    .AddStrataraProjectionGrains());

await silo.DispatchAsync(new DepositCommand(Guid.NewGuid(), 100m));   // runs in the account's activation
await silo.WaitForReadersAsync();                                       // every reader is at the store's head
```

What the host exposes:

- **`DispatchAsync`** sends a command through the mediator under `Session`. Where the aggregate grains are
  registered, the handler runs in its aggregate's activation.
- **`WaitForReadersAsync`** returns once every registered projection and saga has applied the store up to its head
  in every partition. On timeout it names the lagging reader and partition — a projection that throws stops its
  partition.
- **`Timers`** is the `IDurableTimers` of `AddStrataraDurableTimers`. A timer due in a second fires within a few
  seconds, and a process timeout reaches its `SagaProcess<TState>` the same way.
- **`SeedAtHeadAsync`** and **`ResetAsync`** are the execution model's own seeding and reset. Seeding belongs before
  the silo starts, as in a deployment, so the host offers `ExecutionModelTestHostOptions.BeforeStart` for it.
- **`Session`** is the context every scope starts under, `ExecutionModelTestHost.DefaultTenantId` by default.

The periods are `ExecutionModelTestHostOptions`: `PollInterval` 250 ms, `ReminderPeriod` 1 s, `IntentGrace` 2 s,
`DrainPollingInterval` 1 s and four partitions. A value the test sets through a registration's own settings wins.
Creating a host takes a few seconds, so share one across a test class through a fixture and call `ResetAsync`
between tests where one must not see another's state.

The host is one silo. What happens across silos — a kill, a role split, a takeover — needs real infrastructure and
belongs in an integration test. The sample
[`Stratara.Sample.OrleansExecutionModel`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.OrleansExecutionModel)
runs a command, a projection and a process timeout on the host in one console run.

## The test-support packages stay out of running systems

All three test-support packages wire in-memory and development-grade implementations. A host that starts
with them starts successfully and loses every write, which is why the boundary is enforced rather
than merely documented.

**At build time.** Referencing either package from a project that is not a test project fails the
build with `STRATARA1001`. A project counts as a test project when MSBuild reports either
`IsTestProject` or `IsTestingPlatformApplication` as true, so both the Microsoft Testing Platform
and `Microsoft.NET.Test.Sdk` are recognised. For a deliberate exception — a sample, a benchmark —
opt out explicitly:

```xml
<PropertyGroup>
  <StrataraAllowTestSupportOutsideTests>true</StrataraAllowTestSupportOutsideTests>
</PropertyGroup>
```

The check ships inside the package, so it fires on a `PackageReference`. It cannot see a project
reference within a single solution.

**At registration.** `AddStrataraTestingEventStore` throws when a registered `IHostEnvironment`, or
`DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT`, names anything other than `Development`, and
`ExecutionModelTestHost.CreateAsync` throws when either variable does. Where no
environment is stated at all — an ordinary unit test, which has no host — the call is allowed. This
is deliberately not a whitelist: refusing the unstated case would refuse the only legitimate use.

Constructing a double directly (`new InMemoryKeyStore()`) is covered by neither guard.

**Erasure is not simulated.** The development key store (`DummyKeyStore`) derives one key from a
fixed pass-phrase and holds no key material, so `RevokeAsync` and `EraseScopeAsync` throw
`NotSupportedException` rather than returning successfully. Use `InMemoryKeyStore` to exercise
crypto-shredding in a test, or a real key store outside one.

## Test conventions

- **`[Fact]` over `[Theory]`** unless there's genuine data-table variation. A `[Theory]` with 2 rows is usually 2 `[Fact]`s in disguise.
- **`Mock<ILogger>`** from Moq — Stratara doesn't bring its own logger fake.
- **`[ExcludeFromCodeCoverage]`** on pure data classes (DTOs, events, commands/queries, configs). The coverage report should reflect *executable behaviour*, not record-derived getters.
- **`[UsedImplicitly]`** on framework-invoked members (Apply methods, projection handlers, JSON-deserialized setters) — ReSharper / Rider can't see the reflection-driven call sites.
- **No code comments in test bodies.** If a test needs explanation, the explanation goes in the *test name*. `LogChangeSetCreated_DefersFieldNameJoinWhenDebugDisabled` — full sentence, scenario + expected outcome.

## Mocking handlers (the unified `IQueryHandler<TRequest, TResult>`)

Both `ICommand<TResult>` and `IQuery<TResult>` share `IRequest<TResult>` + handle via `IQueryHandler<TRequest, TResult>`. Mock that interface, not `ICommandHandler<T>`:

```csharp
var handler = new Mock<IQueryHandler<MyCommand, Guid>>();
handler.Setup(h => h.HandleAsync(It.IsAny<MyCommand>(), It.IsAny<CancellationToken>()))
       .ReturnsAsync(Guid.NewGuid());
```

## Mocking `SignInManager<TUser>` / `UserManager<TUser>`

Castle.DynamicProxy (Moq's underlying engine) needs **public** TestUser classes. Internal types fail with `ArgumentException: type is not accessible`.

```csharp
public sealed class TestUser : IdentityUser   // public, not internal
{
}
```

## Logger-extension testing

Stratara's source-gen `[LoggerMessage]` extensions all live under `Stratara.Shared.Diagnostics.Extensions` (cross-package convention, intentional). Test the logger setup, not the format string:

<!-- stratara-snippet-ignore: calls a source-generated logger extension the reader writes -->
```csharp
var logger = new Mock<ILogger>();
logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

logger.Object.LogMyEvent(eventId: 1, message: "hello");

logger.Verify(
    l => l.Log(LogLevel.Information, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
    Times.Once);
```

## Integration-test boundary

If your test:

- Needs Docker (a real Postgres / RabbitMQ / Service Bus emulator) → `*IntegrationTests` project.
- Needs only an in-memory store / mock → regular `*Tests` project.

Don't mix. The CI separation depends on the suffix.
