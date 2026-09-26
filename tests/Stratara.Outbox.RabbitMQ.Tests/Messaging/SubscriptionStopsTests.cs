using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Stratara.Outbox.AzureServiceBus.Messaging;
using Stratara.Outbox.RabbitMQ.Messaging;

namespace Stratara.Outbox.RabbitMQ.Tests.Messaging;

/// <summary>
/// How long a stopping subscription may wait for its running handlers, on both transports: until the host's shutdown
/// timeout runs out from the moment the application starts stopping until the host has stopped, and for the standalone
/// bound any other time.
/// </summary>
public sealed class SubscriptionStopsTests
{
    public static TheoryData<string> Transports => new() { "RabbitMQ", "Service Bus" };

    private static readonly TimeSpan JustUnderTheStandaloneBound = TimeSpan.FromSeconds(19);
    private static readonly TimeSpan PastTheStandaloneBound = TimeSpan.FromSeconds(2);

    private sealed record Stops(IHostedLifecycleService Service, Func<CancellationTokenSource> Deadline, Action<Task> Add, Action BusDisposing);

    private static Stops Create(string transport, IHostApplicationLifetime lifetime, TimeProvider? time = null)
    {
        if (transport == "RabbitMQ")
        {
            var rabbit = new RabbitMqSubscriptionStops(NullLogger<RabbitMqSubscriptionStops>.Instance, lifetime, time);
            return new Stops(rabbit, rabbit.Deadline, rabbit.Add, rabbit.BusDisposing);
        }

        var serviceBus = new AzureServiceBusSubscriptionStops(NullLogger<AzureServiceBusSubscriptionStops>.Instance, lifetime, time);
        return new Stops(serviceBus, serviceBus.Deadline, serviceBus.Add, serviceBus.BusDisposing);
    }

    /// <summary>Asserts that <paramref name="deadline"/> runs out with the standalone bound, and not before.</summary>
    private static void RunsOutWithTheStandaloneBound(FakeTimeProvider time, CancellationTokenSource deadline)
    {
        time.Advance(JustUnderTheStandaloneBound);
        Assert.False(deadline.IsCancellationRequested);
        time.Advance(PastTheStandaloneBound);
        Assert.True(deadline.IsCancellationRequested);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Outside_a_host_stop_a_stop_runs_out_with_the_standalone_bound(string transport)
    {
        var time = new FakeTimeProvider();
        var stops = Create(transport, new FakeLifetime(), time);
        await stops.Service.StartedAsync(Ct);

        using var deadline = stops.Deadline();

        RunsOutWithTheStandaloneBound(time, deadline);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task While_the_host_stops_a_stop_outlasts_the_standalone_bound(string transport)
    {
        var time = new FakeTimeProvider();
        var stops = Create(transport, new FakeLifetime(), time);
        using var shutdown = new CancellationTokenSource();
        await stops.Service.StartedAsync(Ct);
        await stops.Service.StoppingAsync(shutdown.Token);

        using var deadline = stops.Deadline();
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(deadline.IsCancellationRequested);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Once_the_host_has_stopped_a_stop_still_under_its_deadline_runs_out_with_the_standalone_bound(string transport)
    {
        var time = new FakeTimeProvider();
        var stops = Create(transport, new FakeLifetime(), time);
        using var shutdown = new CancellationTokenSource();
        await stops.Service.StartedAsync(Ct);
        await stops.Service.StoppingAsync(shutdown.Token);
        using var deadline = stops.Deadline();

        await stops.Service.StoppedAsync(shutdown.Token);

        RunsOutWithTheStandaloneBound(time, deadline);
    }

    /// <summary>
    /// The application was told to stop and the host is disposed without being stopped: the bus's disposal bounds a
    /// stop that took the host's deadline, and a stop that begins afterwards keeps the standalone bound.
    /// </summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_host_disposed_without_being_stopped_leaves_no_stop_without_a_bound(string transport)
    {
        var time = new FakeTimeProvider();
        var lifetime = new FakeLifetime();
        var stops = Create(transport, lifetime, time);
        await stops.Service.StartedAsync(Ct);
        lifetime.StopApplication();
        using var before = stops.Deadline();

        stops.BusDisposing();
        using var after = stops.Deadline();

        time.Advance(JustUnderTheStandaloneBound);
        Assert.False(before.IsCancellationRequested);
        Assert.False(after.IsCancellationRequested);
        time.Advance(PastTheStandaloneBound);
        Assert.True(before.IsCancellationRequested);
        Assert.True(after.IsCancellationRequested);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_bus_disposed_before_its_application_was_told_to_stop_leaves_no_stop_without_a_bound(string transport)
    {
        var time = new FakeTimeProvider();
        var lifetime = new FakeLifetime();
        var stops = Create(transport, lifetime, time);
        await stops.Service.StartedAsync(Ct);

        stops.BusDisposing();
        lifetime.StopApplication();
        using var deadline = stops.Deadline();

        RunsOutWithTheStandaloneBound(time, deadline);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_stop_while_the_host_stops_waits_until_the_hosts_shutdown_timeout_runs_out(string transport)
    {
        var stops = Create(transport, new FakeLifetime());
        using var shutdown = new CancellationTokenSource();
        await stops.Service.StartedAsync(Ct);
        await stops.Service.StoppingAsync(shutdown.Token);

        using var deadline = stops.Deadline();
        Assert.False(deadline.IsCancellationRequested);

        await shutdown.CancelAsync();
        Assert.True(deadline.IsCancellationRequested);
    }

    /// <summary>
    /// A subscription tied to the application's stopping token stops in one of that token's callbacks — the host has not
    /// handed over its shutdown timeout yet, and callbacks registered earlier have not run yet — and still waits under
    /// the host's timeout rather than the standalone bound.
    /// </summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_stop_tied_to_the_applications_stopping_token_waits_under_the_hosts_timeout(string transport)
    {
        var lifetime = new FakeLifetime();
        var stops = Create(transport, lifetime);
        using var shutdown = new CancellationTokenSource();
        await stops.Service.StartedAsync(Ct);
        CancellationTokenSource? deadline = null;
        using var subscription = lifetime.ApplicationStopping.Register(() => deadline = stops.Deadline());

        lifetime.StopApplication();
        await stops.Service.StoppingAsync(shutdown.Token);
        Assert.NotNull(deadline);
        Assert.False(deadline.IsCancellationRequested);

        await shutdown.CancelAsync();
        Assert.True(deadline.IsCancellationRequested);
        deadline.Dispose();
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_stop_before_the_host_stops_keeps_the_standalone_bound(string transport)
    {
        var stops = Create(transport, new FakeLifetime());
        using var shutdown = new CancellationTokenSource();
        await stops.Service.StartedAsync(Ct);
        using var deadline = stops.Deadline();

        await stops.Service.StoppingAsync(shutdown.Token);
        await shutdown.CancelAsync();

        Assert.False(deadline.IsCancellationRequested);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task After_the_host_has_stopped_a_stop_keeps_the_standalone_bound_again(string transport)
    {
        var lifetime = new FakeLifetime();
        var stops = Create(transport, lifetime);
        using var shutdown = new CancellationTokenSource();
        await stops.Service.StartedAsync(Ct);
        lifetime.StopApplication();
        await stops.Service.StoppingAsync(shutdown.Token);
        await stops.Service.StoppedAsync(shutdown.Token);

        using var deadline = stops.Deadline();
        await shutdown.CancelAsync();

        Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>
    /// A host that stopped in time does not cut short a stop that took the host's deadline just before: it keeps the
    /// standalone bound from then on rather than running out at once.
    /// </summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public async Task A_host_that_stopped_in_time_does_not_cut_short_a_stop_still_under_its_deadline(string transport)
    {
        var stops = Create(transport, new FakeLifetime());
        using var shutdown = new CancellationTokenSource();
        await stops.Service.StartedAsync(Ct);
        await stops.Service.StoppingAsync(shutdown.Token);
        using var deadline = stops.Deadline();

        await stops.Service.StoppedAsync(shutdown.Token);

        Assert.False(deadline.IsCancellationRequested);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task When_the_hosts_shutdown_timeout_runs_out_the_host_stops_and_the_stops_give_up(string transport)
    {
        var stops = Create(transport, new FakeLifetime());
        using var shutdown = new CancellationTokenSource();
        await stops.Service.StartedAsync(Ct);
        await stops.Service.StoppingAsync(shutdown.Token);
        using var deadline = stops.Deadline();
        stops.Add(Task.Delay(Timeout.Infinite, deadline.Token));

        var stopped = stops.Service.StoppedAsync(shutdown.Token);
        Assert.False(stopped.IsCompleted);
        await shutdown.CancelAsync();

        await stopped.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(deadline.IsCancellationRequested);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }
}
