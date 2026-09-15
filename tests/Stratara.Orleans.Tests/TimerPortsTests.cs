using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.Sagas;
using Stratara.Orleans.Timers;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The host's timer owner check and handler serve every owner that is not a process's, whichever side of the
/// execution model they were registered on, and a host whose timer ports do not compose fails at start.
/// </summary>
public sealed class TimerPortsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_host_ports_serve_every_other_owner_whichever_order_they_were_registered_in(bool hostFirst)
    {
        var host = new HostTimers();
        var services = new ServiceCollection().AddLogging().AddSingleton(new Mock<IGrainFactory>().Object);
        if (hostFirst)
        {
            AddHost(services, host).AddStrataraSagaGrains();
        }
        else
        {
            AddHost(services.AddStrataraSagaGrains(), host);
        }

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var processOwner = SagaProcessTimerHost.OwnerOf($"TimeoutSaga|{Guid.NewGuid():N}");

        Assert.Same(host, TimerPorts.OwnersFor(scope.ServiceProvider, "order-42"));
        Assert.Same(host, TimerPorts.HandlerFor(scope.ServiceProvider, "order-42"));
        Assert.IsType<SagaProcessTimerHost>(TimerPorts.OwnersFor(scope.ServiceProvider, processOwner));
        Assert.IsType<SagaProcessTimerHost>(TimerPorts.HandlerFor(scope.ServiceProvider, processOwner));
        await StartCheck(provider).StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Processes_without_their_timers_fail_the_start()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddStrataraDurableTimers()
            .AddScoped<ISaga>(_ => new Mock<ISagaProcess>().Object);
        AddHost(services, new HostTimers());
        await using var provider = services.BuildServiceProvider();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartCheck(provider).StartAsync(CancellationToken.None));

        Assert.Contains("AddStrataraSagaGrains", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_owner_checks_of_the_host_fail_the_start()
    {
        var services = new ServiceCollection().AddLogging().AddStrataraDurableTimers();
        AddHost(services, new HostTimers());
        AddHost(services, new HostTimers());
        await using var provider = services.BuildServiceProvider();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartCheck(provider).StartAsync(CancellationToken.None));

        Assert.Contains(nameof(ITimerOwners), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_owner_check_without_a_handler_fails_the_start()
    {
        var services = new ServiceCollection().AddLogging().AddStrataraDurableTimers().AddSingleton<ITimerOwners>(new HostTimers());
        await using var provider = services.BuildServiceProvider();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartCheck(provider).StartAsync(CancellationToken.None));

        Assert.Contains(nameof(ITimerHandler), refused.Message, StringComparison.Ordinal);
    }

    private static IServiceCollection AddHost(IServiceCollection services, HostTimers host) =>
        services.AddSingleton<ITimerOwners>(host).AddSingleton<ITimerHandler>(host);

    private static TimerPortsStartupCheck StartCheck(IServiceProvider provider) =>
        provider.GetServices<IHostedService>().OfType<TimerPortsStartupCheck>().Single();

    private sealed class HostTimers : ITimerOwners, ITimerHandler
    {
        public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task OnDueAsync(TimerDue due, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
