using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.CommitOrder;
using Stratara.Diagnostics;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// A host that read four partitions leaves keep-alive reminders for all four; restarted at two partitions, the reminder
/// of a reader beyond the new count brings it back once, and it retires: it logs the retirement, unregisters the
/// reminder, and reads nothing, so no stall is counted for it (scenario <em>The partition count is lowered under the
/// native reader</em>). The reminders are not reset, which is the situation retirement exists for.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class RetiredReaderTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int SiloPort = 11240;
    private const int GatewayPort = 30129;
    private const int RetiredPartition = 3;
    private static readonly TimeSpan RetirementTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_reader_beyond_a_lowered_partition_count_retires_and_does_not_return()
    {
        var consumer = nameof(TenantCaptureProjection);
        var key = StoreReaderGrainKey.Of(consumer, RetiredPartition);

        using (var before = await StartAsync(partitionCount: 4, new CapturedLogs()))
        {
            Assert.Single(await ReminderRowsAsync(before, key));
            await before.StopAsync();
        }

        var logs = new CapturedLogs();
        using var after = await StartAsync(partitionCount: 2, logs);

        Assert.True(await WaitUntilAsync(() => Task.FromResult(logs.Entries.Any(e => e.EventId == LogEvents.Orleans.StoreReaderRetired && e.Message.Contains($"partition {RetiredPartition} ", StringComparison.Ordinal))), RetirementTimeout),
            "the reader beyond the new partition count never retired");
        Assert.True(await WaitUntilAsync(async () => (await ReminderRowsAsync(after, key)).Count == 0, RetirementTimeout), "the retired reader's keep-alive is still registered");
        Assert.DoesNotContain(logs.Entries, e => e.EventId is LogEvents.Orleans.CatchUpFaulted or LogEvents.Orleans.PartitionStalled);
        Assert.DoesNotContain(logs.Entries, e => e.EventId == LogEvents.Orleans.StoreReaderStarted && e.Message.Contains($"partition {RetiredPartition}.", StringComparison.Ordinal));
        await after.StopAsync();
    }

    private static async Task<List<ReminderEntry>> ReminderRowsAsync(IHost host, string key)
    {
        var grainId = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IProjectionGrain>(key).GetGrainId();
        var rows = await host.Services.GetRequiredService<IReminderTable>().ReadRows(grainId);
        return [.. rows.Reminders];
    }

    private async Task<IHost> StartAsync(int partitionCount, CapturedLogs logs)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_retired_reader_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.Logging.AddProvider(logs);
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, SiloPort, GatewayPort));
        builder.AddEventProjectionServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor("poc_retired_reader_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddSingleton(new TenantCaptures())
            .AddScoped<TenantAtConstruction>()
            .AddScoped<IProjection, TenantCaptureProjection>()
            .AddScoped<TenantCaptureProjection>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .Configure<CommitOrderOptions>(options => options.PartitionCount = partitionCount)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(2);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            });

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    /// <summary>Every log entry the host writes, with its event id and rendered message.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<(int EventId, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<(int EventId, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((eventId.Id, formatter(state, exception)));
        }
    }
}
