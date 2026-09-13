using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Shared.Partitioning;
using Testcontainers.PostgreSql;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// B1 of the expectations: appends per second through the real event source, for the shipped store,
/// the store with the transaction-id column, and the store with the partition counter; with 1, 8 and
/// 32 writers; with streams spread over every bucket and with every writer on one bucket. Three
/// repetitions per setting, every repetition's number in the raw result.
/// </summary>
public static class AppendThroughputRun
{
    private static readonly int[] Writers = [1, 8, 32];
    private static readonly string[] Layouts = ["current", "native", "counter"];
    private static readonly string[] Distributions = ["spread", "one-bucket"];

    public static async Task<int> RunAsync(string evidenceRoot, int appends, int repetitions)
    {
        var run = Evidence.CreateRunDirectory(evidenceRoot, "append-throughput");
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithCommand("-c", "max_connections=400").Build();
        await postgres.StartAsync();
        Evidence.WriteEnvironment(run, new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image }, new { appends, repetitions, Writers, Layouts, Distributions });

        var results = new List<object>();
        foreach (var layout in Layouts)
        {
            foreach (var distribution in Distributions)
            {
                foreach (var writers in Writers)
                {
                    var perRepetition = new List<double>();
                    for (var repetition = 0; repetition < repetitions; repetition++)
                    {
                        var database = $"b1_{layout}_{distribution.Replace('-', '_')}_{writers}_{repetition}";
                        var connectionString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = database }.ConnectionString;
                        var seconds = await MeasureAsync(layout, distribution, writers, appends, connectionString);
                        NpgsqlConnection.ClearAllPools();
                        perRepetition.Add(appends / seconds);
                        Console.WriteLine($"{layout,-8} {distribution,-10} writers {writers,2} rep {repetition + 1}: {appends / seconds,8:F0} appends/s");
                    }

                    results.Add(new { layout, distribution, writers, appends, appendsPerSecond = perRepetition, median = perRepetition.Order().ElementAt(perRepetition.Count / 2) });
                }
            }
        }

        Evidence.WriteResult(run, new { measurement = "append-throughput", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static async Task<double> MeasureAsync(string layout, string distribution, int writers, int appends, string connectionString)
    {
        using var host = await StartStoreAsync(layout, connectionString);
        var streams = StreamsFor(distribution, count: 64);
        var tenantId = Guid.NewGuid();

        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, appends),
            new ParallelOptions { MaxDegreeOfParallelism = writers },
            async (i, ct) =>
            {
                var streamId = distribution == "spread" ? Guid.NewGuid() : streams[i % streams.Count];
                await using var scope = host.Services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
                var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
                if (distribution == "spread")
                {
                    await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId), ct);
                    await events.SaveChangesAsync(ct);
                }
                else
                {
                    await AppendWithRetryAsync(events, streamId, ct);
                }
            });

        return Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    /// <summary>
    /// Writers on one bucket collide on stream versions; a conflict is retried the way the command
    /// worker retries it — the whole append again — and the retries are part of what the setting
    /// measures.
    /// </summary>
    private static async Task AppendWithRetryAsync(IEventSource events, Guid streamId, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                if (await events.ExistsAsync(streamId, cancellationToken))
                {
                    await events.AppendAsync<Counter>(streamId, new CounterIncremented(streamId, 1), cancellationToken);
                }
                else
                {
                    await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId), cancellationToken);
                }

                await events.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (InvalidOperationException)
            {
                // Two writers created the stream at once; append instead.
            }
            catch (ConcurrencyException)
            {
                // Another writer took this version; read the head again and append after it.
            }
        }
    }

    private static List<Guid> StreamsFor(string distribution, int count)
    {
        if (distribution == "spread")
        {
            return [];
        }

        var bucket = BucketCalculator.GetBucketId(Guid.NewGuid());
        var streams = new List<Guid>(count);
        while (streams.Count < count)
        {
            var candidate = Guid.NewGuid();
            if (BucketCalculator.GetBucketId(candidate) == bucket)
            {
                streams.Add(candidate);
            }
        }

        return streams;
    }

    private static async Task<IHost> StartStoreAsync(string layout, string connectionString)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:defaultdb"] = connectionString });
        builder.Services
            .AddSessionContext()
            .AddSecurity()
            .AddMapping()
            .AddEventSourcing()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddScoped<IEventBundleOutboxDispatcher, NoPublishDispatcher>()
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = layout == "counter");

        if (layout == "current")
        {
            builder.Services.AddNpgsqlWriteDbContextFactory<PocWriteDbContext>();
        }
        else
        {
            builder.Services.AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>();
        }

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            if (layout == "current")
            {
                await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
                await context.Database.EnsureCreatedAsync();
            }
            else
            {
                await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, options => options.MaintainPartitionCounter = layout == "counter");
            }
        }

        await host.StartAsync();
        return host;
    }

    /// <summary>The append is what is measured; what happens to the bundle afterwards is not.</summary>
    private sealed class NoPublishDispatcher : IEventBundleOutboxDispatcher
    {
        public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
