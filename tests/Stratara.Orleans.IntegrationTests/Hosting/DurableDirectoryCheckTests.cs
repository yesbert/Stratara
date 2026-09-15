using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using StackExchange.Redis;
using Stratara.Abstractions.Singleton;
using Stratara.Orleans.IntegrationTests.Fixtures;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Task 4.1: a host that registers the execution model's grains without the storage-backed directory
/// they are placed in fails at start with a message naming the registration, and a host that registers
/// the directory through <c>AddStrataraOrleans</c> starts.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DurableDirectoryCheckTests(RedisFixture redis)
{
    [Fact]
    public async Task A_silo_without_the_durable_directory_fails_at_start_naming_the_registration()
    {
        using var host = Build(siloPort: 11241, gatewayPort: 30131, withDirectory: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(timeout.Token));

        var messages = string.Join(" | ", Chain(failure).Select(exception => exception.Message));
        Assert.Contains(GrainDirectories.Durable, messages, StringComparison.Ordinal);
        Assert.Contains("AddStrataraOrleans", messages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silo_with_the_durable_directory_starts()
    {
        using var host = Build(siloPort: 11242, gatewayPort: 30132, withDirectory: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        await host.StartAsync(timeout.Token);
        await host.StopAsync();
    }

    private IHost Build(int siloPort, int gatewayPort, bool withDirectory)
    {
        var redisOptions = ConfigurationOptions.Parse(redis.ConnectionString);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(siloPort, gatewayPort);
            silo.UseInMemoryReminderService();
            if (withDirectory)
            {
                silo.AddStrataraOrleans((s, name) => s.AddRedisGrainDirectory(name, options => options.ConfigurationOptions = redisOptions));
            }
        });
        builder.Services.AddStrataraSingletonWork<IdleWork>(options => options.KeepAlivePeriod = TimeSpan.FromMinutes(1));
        return builder.Build();
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    foreach (var nested in Chain(inner))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    public sealed class IdleWork : ISingletonWork
    {
        public string Name => "directory-check-idle";

        public TimeSpan Period => TimeSpan.FromMinutes(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
