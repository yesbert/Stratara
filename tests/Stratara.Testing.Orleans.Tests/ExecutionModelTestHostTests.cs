using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Timers;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// The scenarios of <em>A test can run the execution model in one process</em>: a command in its aggregate's activation
/// applied by a projection, a timer and a process timeout within seconds, the seeding and the reset, and periods below
/// the runtime's default.
/// </summary>
public sealed class ExecutionModelTestHostTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_command_runs_in_its_aggregates_activation_and_a_projection_applies_it()
    {
        var clock = Stopwatch.StartNew();
        var runs = new Runs();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => Commands(services, runs).AddStrataraProjectionGrains());
        var accountId = Guid.NewGuid();

        await host.DispatchAsync(new OpenAccount(accountId, 100m), TestContext.Current.CancellationToken);
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Activation", runs.Commands[accountId], StringComparison.Ordinal);
        Assert.Equal(100m, runs.Balances[accountId]);
        Assert.True(clock.Elapsed < Budget, $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_timer_fires_within_seconds()
    {
        var clock = Stopwatch.StartNew();
        var runs = new Runs();
        var ports = new TimerPorts(runs);
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services
            .AddSingleton(runs)
            .AddStrataraDurableTimers()
            .AddSingleton<ITimerOwners>(ports)
            .AddSingleton<ITimerHandler>(ports));
        var owner = $"order-{Guid.NewGuid():N}";

        await host.Timers.RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow.AddSeconds(1)), TestContext.Current.CancellationToken);

        Assert.True(await WaitUntilAsync(() => runs.Timers.Any(timer => timer.OwnerId == owner)), "the timer did not fire");
        Assert.True(await WaitUntilAsync(() => host.Timers.ListAsync(owner).Result.Count == 0), "the timer is still registered");
        Assert.True(clock.Elapsed < Budget, $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_process_timeout_fires_with_the_state_as_recorded()
    {
        var clock = Stopwatch.StartNew();
        var runs = new Runs();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => Commands(services, runs)
            .AddTrustedType<WelcomeState>()
            .AddTrustedType<WelcomeScheduled>()
            .AddScoped<ISaga, WelcomeProcess>()
            .AddStrataraSagaGrains());
        var accountId = Guid.NewGuid();

        await host.DispatchAsync(new OpenAccount(accountId, 42m), TestContext.Current.CancellationToken);

        Assert.True(await WaitUntilAsync(() => runs.Timeouts.ContainsKey(accountId)), "the process timeout did not fire");
        Assert.Equal(42m, runs.Timeouts[accountId]);
        Assert.True(clock.Elapsed < Budget, $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_test_seeds_and_resets()
    {
        var runs = new Runs();
        var ports = new TimerPorts(runs);
        var before = Guid.NewGuid();
        var seeded = 0;
        await using var host = await ExecutionModelTestHost.CreateAsync(
            services => Commands(services, runs)
                .AddStrataraProjectionGrains()
                .AddStrataraDurableTimers()
                .AddSingleton<ITimerOwners>(ports)
                .AddSingleton<ITimerHandler>(ports),
            options => options.BeforeStart = async unstarted =>
            {
                await AppendOpenedAsync(unstarted, before, 1m);
                seeded = (await unstarted.SeedAtHeadAsync()).Seeded;
            });
        var after = Guid.NewGuid();

        await host.DispatchAsync(new OpenAccount(after, 2m), TestContext.Current.CancellationToken);
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(seeded > 0);
        Assert.False(runs.Balances.ContainsKey(before), "the projection applied an entry appended before the seeding");
        Assert.Equal(2m, runs.Balances[after]);

        var owner = $"order-{Guid.NewGuid():N}";
        await host.Timers.RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        var report = await host.ResetAsync(TestContext.Current.CancellationToken);

        Assert.True(report.Checkpoints > 0, "the reset removed no checkpoint");
        Assert.True(report.Reminders > 0, "the reset removed no timer");
        Assert.Empty(await host.Timers.ListAsync(owner, TestContext.Current.CancellationToken));
        await using var scope = host.Services.CreateAsyncScope();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        var reader = scope.ServiceProvider.GetRequiredService<Stratara.Abstractions.CommitOrder.ICommittedPositionReader>();
        var name = scope.ServiceProvider.GetRequiredService<IProjectionHandler>().GetProjectionName(new BalanceProjection(runs));
        for (var partition = 0; partition < 4; partition++)
        {
            Assert.Equal(
                await reader.HeadAsync(partition, TestContext.Current.CancellationToken),
                await checkpoints.GetAsync(name, partition, reader.Name, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// The reset between two tests of one host: the readers keep running, so a reset that only removed the
    /// checkpoints would leave them at the position they had cached — waiting for readers would then never end, and a
    /// reader that did read again would apply the first test's entries a second time.
    /// </summary>
    [Fact]
    public async Task A_host_reset_between_two_tests_reads_on_without_applying_anything_twice()
    {
        var runs = new Runs();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => Commands(services, runs).AddStrataraProjectionGrains());
        var first = Guid.NewGuid();
        await host.DispatchAsync(new OpenAccount(first, 1m), TestContext.Current.CancellationToken);
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1m, runs.Balances[first]);
        var appliedBefore = runs.Applied;

        await host.ResetAsync(TestContext.Current.CancellationToken);

        // Nothing new has been committed: the readers are at the head, so the wait ends at once.
        await host.WaitForReadersAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(appliedBefore, runs.Applied);

        var second = Guid.NewGuid();
        await host.DispatchAsync(new OpenAccount(second, 2m), TestContext.Current.CancellationToken);
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2m, runs.Balances[second]);
        Assert.Equal(appliedBefore + 1, runs.Applied);
    }

    [Fact]
    public async Task A_test_shortens_a_period_to_one_second()
    {
        var runs = new Runs();
        await using var host = await ExecutionModelTestHost.CreateAsync(
            services => Commands(services, runs).AddStrataraProjectionGrains(o =>
            {
                o.PollInterval = TimeSpan.FromSeconds(1);
                o.KeepAlivePeriod = TimeSpan.FromSeconds(1);
            }),
            options => options.ReminderPeriod = TimeSpan.FromSeconds(1));
        var accountId = Guid.NewGuid();

        await host.DispatchAsync(new OpenAccount(accountId, 7m), TestContext.Current.CancellationToken);
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(7m, runs.Balances[accountId]);
    }

    private static IServiceCollection Commands(IServiceCollection services, Runs runs) => services
        .AddSingleton(runs)
        .AddAggregatesFromAssemblyContaining<Account>()
        .AddTrustedType<OpenAccount>()
        .AddScoped<Stratara.Abstractions.Mediator.ICommandHandler<OpenAccount>, OpenAccountHandler>()
        .AddScoped<IProjection, BalanceProjection>()
        .AddStrataraAggregateGrains();

    private static async Task AppendOpenedAsync(ExecutionModelTestHost host, Guid accountId, decimal balance)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await events.CreateAsync<Account>(accountId, new AccountOpened(accountId, ExecutionModelTestHost.DefaultTenantId, balance));
        await events.SaveChangesAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }
}
