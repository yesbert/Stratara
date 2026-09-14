using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Outbox.RabbitMQ.Outbox;
using Testcontainers.PostgreSql;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// C1 of <c>close-the-gap-between-commit-and-publish</c>: appends per second through the real event
/// source and the real bundle dispatcher with a bus that accepts instantly, bus-first against
/// durable bundles, streams spread over the buckets, 1 / 8 / 32 writers, three repetitions of the
/// given number of appends. What the durable path adds is one insert in the commit and one delete
/// after acceptance; the bus double removes the broker from the comparison.
/// </summary>
public static class DurableBundlesRun
{
    private static readonly int[] Writers = [1, 8, 32];
    private static readonly string[] Layouts = ["bus-first", "durable"];

    public static async Task<int> RunAsync(string evidenceRoot, int appends, int repetitions)
    {
        var run = Evidence.CreateRunDirectory(evidenceRoot, "durable-bundles");
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithCommand("-c", "max_connections=400").Build();
        await postgres.StartAsync();
        Evidence.WriteEnvironment(run, new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image }, new { appends, repetitions, Writers, Layouts });

        var results = new List<object>();
        foreach (var layout in Layouts)
        {
            foreach (var writers in Writers)
            {
                var perRepetition = new List<double>();
                for (var repetition = 0; repetition < repetitions; repetition++)
                {
                    var database = $"c1_{layout.Replace('-', '_')}_{writers}_{repetition}";
                    var connectionString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = database }.ConnectionString;
                    var seconds = await MeasureAsync(layout == "durable", writers, appends, connectionString);
                    NpgsqlConnection.ClearAllPools();
                    perRepetition.Add(appends / seconds);
                    Console.WriteLine($"{layout,-9} writers {writers,2} rep {repetition + 1}: {appends / seconds,8:F0} appends/s");
                }

                results.Add(new { layout, writers, appends, appendsPerSecond = perRepetition, median = perRepetition.Order().ElementAt(perRepetition.Count / 2) });
            }
        }

        Evidence.WriteResult(run, new { measurement = "durable-bundles", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static async Task<double> MeasureAsync(bool durable, int writers, int appends, string connectionString)
    {
        using var host = await StartStoreAsync(durable, connectionString);
        var tenantId = Guid.NewGuid();

        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, appends),
            new ParallelOptions { MaxDegreeOfParallelism = writers },
            async (_, ct) =>
            {
                var streamId = Guid.NewGuid();
                await using var scope = host.Services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
                var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
                await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId), ct);
                await events.SaveChangesAsync(ct);
            });

        return Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    private static async Task<IHost> StartStoreAsync(bool durable, string connectionString)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = connectionString,
            ["Outbox:DurableBundles"] = durable ? "true" : "false",
        });
        builder.AddMessaging();
        builder.Services
            .AddSessionContext()
            .AddSecurity()
            .AddMapping()
            .AddResiliencePipelines()
            .AddEventSourcing()
            .AddOutboxDispatcher()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IMessageBus, AcceptingBus>());

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        await host.StartAsync();
        return host;
    }

    /// <summary>A bus that accepts every publish at once, so the comparison measures the store, not the broker.</summary>
    private sealed class AcceptingBus : IMessageBus
    {
        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
