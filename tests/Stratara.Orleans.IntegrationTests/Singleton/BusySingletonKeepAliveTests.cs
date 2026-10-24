using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Stratara.Abstractions.Singleton;
using Stratara.Orleans.IntegrationTests.Aggregates;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.Singleton;

/// <summary>
/// Scenarios <em>A singleton work runs longer than the response timeout</em> and <em>A silo starts while a singleton
/// work runs long</em>: under a two-second response timeout and a one-second keep-alive, a work whose first run takes
/// fifteen seconds runs once to its end, no two runs overlap, the silo logs no reminder tick it could not deliver, and a
/// second silo that registers the same work starts during that run without waiting for or interrupting it.
/// </summary>
/// <remarks>
/// The absence of the runtime's lines is read from their wording, which Orleans may change; the outcome — one long run,
/// never two at once — is asserted beside it.
/// </remarks>
[Collection(InfrastructureCollection.Name)]
public sealed class BusySingletonKeepAliveTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(15);

    // Long enough that a second silo's start, membership join included, ends inside it on a slow machine too.
    private static readonly TimeSpan StartHold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] RuntimeReminderErrors = ["Could not deliver reminder tick", "Response did not arrive on time"];

    [Fact]
    public async Task A_singleton_work_that_runs_longer_than_the_response_timeout_answers_its_keep_alive()
    {
        var probe = new LongRunProbe(Hold);
        var logs = new RuntimeLogs();
        var cluster = $"stratara-poc-busy-singleton-{Guid.NewGuid():N}";
        using var app = await StartSiloAsync(probe, logs, cluster, siloPort: 11532, gatewayPort: 30532);

        Assert.True(await WaitUntilAsync(() => probe.Runs >= 2), "the work did not run again after its long run");
        await Task.Delay(ResponseTimeout * 2, TestContext.Current.CancellationToken);
        await app.StopAsync();

        Assert.Equal(1, probe.MaxConcurrent);
        Assert.Empty(logs.Matching(RuntimeReminderErrors));
    }

    [Fact]
    public async Task A_silo_that_starts_while_a_singleton_work_runs_long_starts_without_waiting_for_it()
    {
        var probe = new LongRunProbe(StartHold);
        var firstLogs = new RuntimeLogs();
        var secondLogs = new RuntimeLogs();
        var cluster = $"stratara-poc-busy-singleton-start-{Guid.NewGuid():N}";
        using var first = await StartSiloAsync(probe, firstLogs, cluster, siloPort: 11533, gatewayPort: 30533);
        Assert.True(await WaitUntilAsync(() => probe.LongRunStarted), "the long run never started");

        var startedAt = DateTimeOffset.UtcNow;
        using var second = await StartSiloAsync(probe, secondLogs, cluster, siloPort: 11534, gatewayPort: 30534);
        var startTook = DateTimeOffset.UtcNow - startedAt;

        Assert.False(probe.LongRunEnded, $"the second silo's start took {startTook} and ended after the long run, so the test proves nothing");
        Assert.True(await WaitUntilAsync(() => probe.LongRunEnded || probe.LongRunCancelled), "the long run neither ended nor was cancelled");
        Assert.False(probe.LongRunCancelled, "the second silo's start interrupted the long run");
        Assert.True(await WaitUntilAsync(() => probe.Runs >= 2), "the work did not run again after its long run");
        await Task.Delay(ResponseTimeout * 2, TestContext.Current.CancellationToken);
        await second.StopAsync();
        await first.StopAsync();

        Assert.Equal(1, probe.MaxConcurrent);
        Assert.Empty(firstLogs.Matching(RuntimeReminderErrors));
        Assert.Empty(secondLogs.Matching(RuntimeReminderErrors));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private async Task<IHost> StartSiloAsync(LongRunProbe probe, RuntimeLogs logs, string clusterId, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.AddProvider(logs);
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort, clusterId: clusterId)
            .Configure<SiloMessagingOptions>(options => options.ResponseTimeout = ResponseTimeout));
        builder.Services
            .AddSingleton(probe)
            .AddStrataraSingletonWork<LongRunWork>(options => options.KeepAlivePeriod = KeepAlive);

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    /// <summary>
    /// Makes the first run of the work last a fixed time, records whether it ended or was cancelled, and counts the runs
    /// and how many ran at once — a run that started twice would overlap or follow a cancellation.
    /// </summary>
    public sealed class LongRunProbe(TimeSpan hold)
    {
        private int _runs;
        private int _longRuns;
        private int _inFlight;
        private int _maxConcurrent;
        private volatile bool _longRunStarted;
        private volatile bool _longRunEnded;
        private volatile bool _longRunCancelled;

        public int Runs => Volatile.Read(ref _runs);

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public bool LongRunStarted => _longRunStarted;

        public bool LongRunEnded => _longRunEnded;

        public bool LongRunCancelled => _longRunCancelled;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            int current;
            while (inFlight > (current = Volatile.Read(ref _maxConcurrent)))
            {
                Interlocked.CompareExchange(ref _maxConcurrent, inFlight, current);
            }

            try
            {
                if (Interlocked.CompareExchange(ref _longRuns, 1, 0) == 0)
                {
                    _longRunStarted = true;
                    try
                    {
                        await Task.Delay(hold, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        _longRunCancelled = true;
                        throw;
                    }

                    _longRunEnded = true;
                }

                Interlocked.Increment(ref _runs);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    public sealed class LongRunWork(LongRunProbe probe) : ISingletonWork
    {
        public string Name => "long-run";

        public TimeSpan Period => TimeSpan.FromMilliseconds(500);

        public Task RunAsync(CancellationToken cancellationToken) => probe.RunAsync(cancellationToken);
    }
}
