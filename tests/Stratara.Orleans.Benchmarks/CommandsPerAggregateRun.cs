using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Diagnostics;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// B3 of the expectations: aggregate-scoped commands whose handler appends to the aggregate's
/// stream, on the bus worker and on the aggregate grain, in the durable-intent shape and the
/// synchronous one; distributed over 2 000 aggregates, 20, and one. Throughput is commands
/// completed per second from the first dispatch to the last completion; conflicts are what the
/// event source's own counter records. One process hosts the worker and the dispatcher, which is the
/// fair comparison of serialisation and the reason no cross-process requeue storm appears here.
/// </summary>
public static class CommandsPerAggregateRun
{
    private static readonly (string Name, int Aggregates)[] Distributions = [("2000x1", 2000), ("20x100", 20), ("1x2000", 1)];
    private static readonly string[] Paths = ["bus-worker", "grain-intent", "grain-sync"];

    public static async Task<int> RunAsync(string evidenceRoot, int commands, int repetitions, PocSiloDirectory directory)
    {
        var run = Evidence.CreateRunDirectory(evidenceRoot, "commands-per-aggregate");
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithCommand("-c", "max_connections=400").Build();
        await using var redis = new RedisBuilder(RedisFixture.Image).Build();
        await using var rabbit = new RabbitMqBuilder(RabbitMqFixture.Image).Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), rabbit.StartAsync());
        Evidence.WriteEnvironment(run,
            new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image, ["redis"] = RedisFixture.Image, ["rabbitmq"] = RabbitMqFixture.Image },
            new { commands, repetitions, Distributions, Paths, profile = PocSiloProfile.Production.ToString(), directory = directory.ToString() });

        var results = new List<object>();
        var port = 0;
        foreach (var path in Paths)
        {
            foreach (var (distribution, aggregates) in Distributions)
            {
                for (var repetition = 0; repetition < repetitions; repetition++)
                {
                    port++;
                    var store = Database(postgres.GetConnectionString(), $"b3_{path.Replace('-', '_')}_{distribution}_{repetition}");
                    var (seconds, conflicts) = await MeasureAsync(path, aggregates, commands, store, postgres.GetConnectionString(), redis.GetConnectionString(), rabbit.GetConnectionString(), 11300 + port, 30200 + port, directory);
                    Console.WriteLine($"{path,-13} {distribution,-7} rep {repetition + 1}: {commands / seconds,8:F0} commands/s, {conflicts} conflicts");
                    results.Add(new { path, distribution, aggregates, commands, repetition, seconds, commandsPerSecond = commands / seconds, conflicts, directory = directory.ToString() });
                }
            }
        }

        Evidence.WriteResult(run, new { measurement = "commands-per-aggregate", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static async Task<(double Seconds, long Conflicts)> MeasureAsync(
        string path, int aggregates, int commands, string store, string adminConnectionString, string redis, string rabbit, int siloPort, int gatewayPort, PocSiloDirectory directory)
    {
        var completed = new CompletionCounter();
        using var host = await StartAsync(path, store, adminConnectionString, redis, rabbit, siloPort, gatewayPort, completed, directory);
        var aggregateIds = Enumerable.Range(0, aggregates).Select(_ => Guid.NewGuid()).ToList();

        long conflicts = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "event_source.append.conflicts")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref conflicts, value));
        listener.Start();

        var started = Stopwatch.GetTimestamp();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            if (path == "grain-sync")
            {
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var inFlight = new List<Task>(commands);
                for (var i = 0; i < commands; i++)
                {
                    inFlight.Add(mediator.HandleAsync(new IncrementAggregate(aggregateIds[i % aggregates])));
                }

                await Task.WhenAll(inFlight);
            }
            else
            {
                var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
                for (var i = 0; i < commands; i++)
                {
                    await dispatcher.EnqueueCommandAsync(new IncrementAggregate(aggregateIds[i % aggregates]));
                }
            }
        }

        await completed.WaitForAsync(commands, TimeSpan.FromMinutes(5));
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        listener.RecordObservableInstruments();
        await host.StopAsync();
        return (seconds, Interlocked.Read(ref conflicts));
    }

    private static async Task<IHost> StartAsync(string path, string store, string adminConnectionString, string redis, string rabbit, int siloPort, int gatewayPort, CompletionCounter completed, PocSiloDirectory directory)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit,
        });

        if (path == "bus-worker")
        {
            builder.AddCommandWorkerServices();
        }
        else
        {
            var orleans = Database(adminConnectionString, "b3_orleans");
            await PocSilo.EnsureSchemaAsync(orleans);
            builder.AddBackendServices();
            builder.UseOrleans(silo => PocSilo.Configure(silo, orleans, redis, siloPort, gatewayPort, PocSiloProfile.Production, directory));
            builder.Services.AddEventSourcing().AddStrataraAggregateGrains();
            if (path == "grain-intent")
            {
                builder.Services.AddStrataraOrleansCommandDispatcher();
            }
        }

        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<IncrementAggregate>, IncrementAggregateHandler>()
            .AddTrustedType<IncrementAggregate>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddSingleton(completed)
            .AddScoped<IEventBundleOutboxDispatcher, NoPublishDispatcher>();

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        await host.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));
        return host;
    }

    private static string Database(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    public sealed class CompletionCounter
    {
        private long _count;

        public void Increment() => Interlocked.Increment(ref _count);

        public async Task WaitForAsync(long expected, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (Interlocked.Read(ref _count) < expected)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    Console.WriteLine($"  only {Interlocked.Read(ref _count)} of {expected} commands completed within {timeout}");
                    return;
                }

                await Task.Delay(20);
            }
        }
    }

    private sealed class NoPublishDispatcher : IEventBundleOutboxDispatcher
    {
        public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed record IncrementAggregate(Guid AggregateId) : ICommand, IAggregateScopedCommand;

/// <summary>The handler under measurement: one real append to the aggregate's stream per command.</summary>
public sealed class IncrementAggregateHandler(IEventSource events, CommandsPerAggregateRun.CompletionCounter completed) : ICommandHandler<IncrementAggregate>
{
    public async Task HandleAsync(IncrementAggregate command, CancellationToken cancellationToken)
    {
        if (await events.ExistsAsync(command.AggregateId, cancellationToken))
        {
            await events.AppendAsync<Counter>(command.AggregateId, new CounterIncremented(command.AggregateId, 1), cancellationToken);
        }
        else
        {
            await events.CreateAsync<Counter>(command.AggregateId, new CounterCreated(command.AggregateId), cancellationToken);
        }

        await events.SaveChangesAsync(cancellationToken);
        completed.Increment();
    }
}
