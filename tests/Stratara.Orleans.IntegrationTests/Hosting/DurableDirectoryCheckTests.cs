using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using StackExchange.Redis;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Singleton;
using Stratara.Contracts.Messages;
using Stratara.Orleans.IntegrationTests.Fixtures;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Task 4.1: a host that registers the execution model's grains without the storage-backed directory
/// they are placed in fails at start with a message naming the registration, and a host that registers
/// the directory through <c>AddStrataraOrleans</c> starts. A silo that registers the directory itself and hosts a
/// singleton work fails naming the work and the call that publishes it; one that hosts nothing placed by role starts.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DurableDirectoryCheckTests(RedisFixture redis)
{
    [Fact]
    public async Task A_silo_without_the_durable_directory_fails_at_start_naming_the_registration()
    {
        using var host = Build(siloPort: 11245, gatewayPort: 30135, DirectoryRegistration.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(timeout.Token));

        var messages = string.Join(" | ", Chain(failure).Select(exception => exception.Message));
        Assert.Contains(GrainDirectories.Durable, messages, StringComparison.Ordinal);
        Assert.Contains("AddStrataraOrleans", messages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silo_with_the_durable_directory_starts()
    {
        using var host = Build(siloPort: 11242, gatewayPort: 30132, DirectoryRegistration.ThroughStratara);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        var failure = await Record.ExceptionAsync(() => host.StartAsync(timeout.Token));
        await host.StopAsync();

        Assert.Null(failure);
    }

    [Fact]
    public async Task A_silo_that_registers_the_directory_itself_and_hosts_a_work_fails_naming_the_work_and_the_call()
    {
        using var host = Build(siloPort: 11330, gatewayPort: 30220, DirectoryRegistration.Direct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(timeout.Token));

        var messages = string.Join(" | ", Chain(failure).Select(exception => exception.Message));
        Assert.Contains(nameof(IdleWork), messages, StringComparison.Ordinal);
        Assert.Contains("AddStrataraOrleans", messages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silo_that_registers_the_directory_itself_with_the_dispatcher_only_starts()
    {
        using var host = Build(siloPort: 11331, gatewayPort: 30221, DirectoryRegistration.Direct, dispatcherOnly: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        var failure = await Record.ExceptionAsync(() => host.StartAsync(timeout.Token));
        await host.StopAsync();

        Assert.Null(failure);
    }

    private IHost Build(int siloPort, int gatewayPort, DirectoryRegistration directory, bool dispatcherOnly = false)
    {
        var redisOptions = ConfigurationOptions.Parse(redis.ConnectionString);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(siloPort, gatewayPort);
            silo.UseInMemoryReminderService();
            switch (directory)
            {
                case DirectoryRegistration.ThroughStratara:
                    silo.AddStrataraOrleans((s, name) => s.AddRedisGrainDirectory(name, options => options.ConfigurationOptions = redisOptions));
                    break;
                case DirectoryRegistration.Direct:
                    silo.AddRedisGrainDirectory(GrainDirectories.Durable, options => options.ConfigurationOptions = redisOptions);
                    break;
            }
        });
        if (dispatcherOnly)
        {
            // The dispatcher's serializer and signer are the host's; nothing is dispatched, so none is registered.
            builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true }));
            builder.Services.AddSingleton<ICommandIntentStore, UnusedIntentStore>().AddStrataraOrleansCommandDispatcher();
        }
        else
        {
            builder.Services.AddStrataraSingletonWork<IdleWork>(options => options.KeepAlivePeriod = TimeSpan.FromMinutes(1));
        }

        return builder.Build();
    }

    private enum DirectoryRegistration
    {
        None,
        ThroughStratara,
        Direct,
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

    /// <summary>An intent store the dispatcher-only silo requires at start and never uses.</summary>
    private sealed class UnusedIntentStore : ICommandIntentStore
    {
        public Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RecordedIntent>> GetDueAsync(DateTimeOffset handedOverBefore, int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryClaimAsync(Guid intentId, DateTimeOffset? expectedLastHandedOverAt, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RenewAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordFailureAsync(Guid intentId, string failure, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task KeepAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
