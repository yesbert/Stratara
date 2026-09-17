# Stratara.Testing.Orleans

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

Run the **Orleans execution model** in a test's own process. `ExecutionModelTestHost` starts one
silo clustered with itself, with reminders and the grain directory in memory, and the framework's
real write stack, portable commit-order reader, checkpoint store and intent store on an in-memory
SQLite database. Every period the model keeps as a reminder or a poll is shortened to seconds, so a
projection applies and a timer fires within a test. No cluster, no broker, no database server, no
Docker.

Builds on [`Stratara.Testing.EntityFrameworkCore`](https://www.nuget.org/packages/Stratara.Testing.EntityFrameworkCore).

## Why run the real grains?

Because the registration is what a test should prove. Your handlers, projections, sagas, processes
and timer ports are registered with the calls production uses — `AddStrataraAggregateGrains`,
`AddStrataraProjectionGrains`, `AddStrataraSagaGrains`, `AddStrataraDurableTimers`,
`AddStrataraOrleansCommandDispatcher` — and run in the same grains, through the same mediator
pipeline, reading the store in commit order from a checkpoint.

## Example

```csharp
await using var host = await ExecutionModelTestHost.CreateAsync(services => services
    .AddAggregatesFromAssemblyContaining<Account>()
    .AddCommandHandlersFromAssemblyContaining<OpenAccountHandler>()
    .AddProjectionsFromAssemblyContaining<BalanceProjection>()
    .AddStrataraAggregateGrains()
    .AddStrataraProjectionGrains());

await host.DispatchAsync(new OpenAccount(accountId, 100m));  // runs in the account's activation
await host.WaitForReadersAsync();                            // every reader is at the store's head

Assert.Equal(100m, balances[accountId]);
```

## Contents

- `ExecutionModelTestHost` — `CreateAsync(configure, options)`, `DispatchAsync`, `WaitForReadersAsync`,
  `Timers`, `SeedAtHeadAsync`, `ResetAsync`, the preset `Session` and `Services`. `IAsyncDisposable`.
- `ExecutionModelTestHostOptions` — the periods (`PollInterval` 250 ms, `ReminderPeriod` 1 s,
  `IntentGrace` 2 s, `DrainPollingInterval` 1 s), the partition count (4), the start timeout, and
  `BeforeStart`, which runs before the silo starts — where a test appends history and seeds the
  readers at its head, as a deployment does while no silo runs. A value a test sets through a
  registration's own settings wins over the host's.
- `StrataraTestReadDbContext` — the read context that holds the checkpoints.

## Share a host across a test class

Creating a host takes a few seconds. Share one through a class fixture and reset it between tests
where one must not see another's state:

```csharp
public sealed class ExecutionModelFixture : IAsyncLifetime
{
    public ExecutionModelTestHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync() =>
        Host = await ExecutionModelTestHost.CreateAsync(services => services /* the roles */);

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
```

`ResetAsync` runs the execution model's reset port — the timers, the grain directory's entries and
the registered readers' checkpoints — — on this host, where the silo keeps running. It stops the readers, puts each of them at the store's head and starts
them again: the
next test's facts are applied, the last test's are not applied a second time, and `WaitForReadersAsync`
returns at once. The read models themselves are not emptied — empty or rebuild them in the fixture
where a test needs them clean.

## Where the host stops

- **One silo.** Membership, reminders and the directory are in memory and not shared. What happens
  across silos — a kill, a role split, a takeover — is an integration test against real infrastructure.
- **Test projects only.** Referencing the package from a project that is not a test project fails
  the build with `STRATARA1001`; `StrataraAllowTestSupportOutsideTests=true` opts out, as the sample
  does. `CreateAsync` refuses to run where `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT` states an
  environment other than `Development`.

## Dependencies

- `Stratara.Orleans`, `Stratara.Orleans.EntityFrameworkCore`, `Stratara.Testing.EntityFrameworkCore`
- `Microsoft.Orleans.Server`, `Microsoft.Extensions.Hosting`
