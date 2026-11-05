using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Session;
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

    /// <summary>
    /// A saga whose dependency refuses to be built without a session — a tenant-routed context — cannot be built at
    /// start, where no session is set; the saga reader tolerates that, and so does this check.
    /// </summary>
    [Fact]
    public async Task A_saga_role_host_whose_sagas_need_a_session_to_be_built_starts()
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(new Mock<IGrainFactory>().Object).AddStrataraSagaGrains();
        AddHost(services, new HostTimers());
        AddSessionBoundSagas(services);
        await using var provider = services.BuildServiceProvider();

        await StartCheck(provider).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_host_without_saga_grains_whose_saga_needs_a_session_to_be_built_starts()
    {
        var services = new ServiceCollection().AddLogging().AddStrataraDurableTimers().AddScoped<ISaga, SessionBoundSaga>();
        AddHost(services, new HostTimers());
        services.AddScoped<SessionBoundContext>(_ => throw new SessionRequiredException());
        await using var provider = services.BuildServiceProvider();

        await StartCheck(provider).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_process_that_needs_a_session_to_be_built_still_fails_the_start_without_its_timers()
    {
        var services = new ServiceCollection().AddLogging().AddStrataraDurableTimers();
        AddHost(services, new HostTimers());
        AddSessionBoundSagas(services);
        await using var provider = services.BuildServiceProvider();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartCheck(provider).StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(nameof(SessionBoundProcess), refused.Message, StringComparison.Ordinal);
        Assert.Contains("AddStrataraSagaGrains", refused.Message, StringComparison.Ordinal);
    }

    private static void AddSessionBoundSagas(IServiceCollection services) =>
        services
            .AddScoped<SessionBoundContext>(_ => throw new SessionRequiredException())
            .AddScoped<ISaga, SessionBoundSaga>()
            .AddScoped<ISaga, SessionBoundProcess>();

    private static IServiceCollection AddHost(IServiceCollection services, HostTimers host) =>
        services.AddSingleton<ITimerOwners>(host).AddSingleton<ITimerHandler>(host);

    private static TimerPortsStartupCheck StartCheck(IServiceProvider provider) =>
        provider.GetServices<IHostedService>().OfType<TimerPortsStartupCheck>().Single();

    private sealed class SessionBoundContext;

    private sealed class SessionBoundSaga(SessionBoundContext context) : ISaga
    {
        public SessionBoundContext Context { get; } = context;
    }

    private sealed class SessionBoundProcess(SessionBoundContext context) : SagaProcess<SessionBoundState>
    {
        public SessionBoundContext Context { get; } = context;

        public override bool Handles(IEvent @event) => false;

        public override Guid CorrelationOf(IEvent @event) => Guid.Empty;

        public override Task HandleAsync(SessionBoundState state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task OnTimeoutAsync(SessionBoundState state, string purpose, ISagaProcessContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class SessionBoundState : ISagaProcessState
    {
        public bool Completed => false;
    }

    private sealed class HostTimers : ITimerOwners, ITimerHandler
    {
        public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task OnDueAsync(TimerDue due, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
