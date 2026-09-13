using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

public enum ProjectionPath
{
    /// <summary>Bundles are published to the bus and the projection worker consumes them — today's path.</summary>
    Bus,

    /// <summary>Bundles nudge the projection grains, which read the store from a checkpoint.</summary>
    Grain,

    /// <summary>Both: the bus worker applies the push, the grains read the store; whichever comes first wins, idempotently.</summary>
    Hybrid,
}

/// <summary>
/// T1 of the expectations: a host that appends events and projects them on one of the two paths,
/// with a bundle dispatcher that can be armed to end the process the moment it is called — which is
/// after the commit and before any publish or nudge. Commands: <c>arm-kill</c>,
/// <c>append streamId</c>, <c>view streamId timeoutMs</c>.
/// </summary>
public sealed class ProjectionScenario(ProjectionPath path) : IPocScenario
{
    public async Task<IHost> BuildAsync(PocHostSettings settings)
    {
        if (path != ProjectionPath.Bus)
        {
            await PocSilo.EnsureSchemaAsync(settings.OrleansConnectionString);
        }

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = settings.StoreConnectionString,
            ["ConnectionStrings:rabbitmq"] = settings.RabbitConnectionString,
        });

        builder.AddEventProjectionWorkerServices();
        builder.Services
            .AddEventSourcing()
            .AddOutboxDispatcher()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(settings.ReadStoreConnectionString)
            .AddAggregatesFromAssemblyContaining<ProjectionScenario>()
            .AddTrustedType<Counter>()
            .AddProjectionsFromAssemblyContaining<ProjectionScenario>()
            .AddSingleton(new ProjectionProbeControl())
            .AddSingleton(new KillSwitch())
            .AddScoped<IProjectionViewTruncator, ProbeViewTruncator>()
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = false);

        if (path != ProjectionPath.Bus)
        {
            builder.UseOrleans(silo => PocSilo.Configure(silo, settings.OrleansConnectionString, settings.RedisConnectionString, settings.SiloPort, settings.GatewayPort));
            builder.Services
                .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
                .AddStrataraProjectionGrains<PocReadDbContext>(
                    options =>
                    {
                        options.PollInterval = TimeSpan.FromSeconds(2);
                        options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
                    },
                    hybrid: path == ProjectionPath.Hybrid);
        }

        KillingBundleDispatcher.Wrap(builder.Services);

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
        }

        return host;
    }

    public async Task<string> HandleAsync(IServiceProvider services, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "arm-kill":
                services.GetRequiredService<KillSwitch>().Armed = true;
                return "ok";
            case "append":
            {
                var streamId = Guid.Parse(parts[1]);
                await using var scope = services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
                var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
                await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
                await events.SaveChangesAsync();
                return "ok";
            }
            case "view":
            {
                var streamId = Guid.Parse(parts[1]);
                var timeout = TimeSpan.FromMilliseconds(int.Parse(parts[2], CultureInfo.InvariantCulture));
                var deadline = DateTimeOffset.UtcNow + timeout;
                while (true)
                {
                    await using var scope = services.CreateAsyncScope();
                    await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
                    if (await read.CounterViews.AsNoTracking().AnyAsync(v => v.StreamId == streamId))
                    {
                        return "present";
                    }

                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        return "absent";
                    }

                    await Task.Delay(200);
                }
            }
            default:
                return "error unknown command " + parts[0];
        }
    }
}

public sealed class KillSwitch
{
    public volatile bool Armed;
}

/// <summary>What the framework's full replay needs from the host: a way to empty every read model at once.</summary>
public sealed class ProbeViewTruncator(IDbContextFactory<PocReadDbContext> contextFactory) : Stratara.Abstractions.Projections.IProjectionViewTruncator
{
    public async Task TruncateAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.CounterViews.ExecuteDeleteAsync(cancellationToken);
        await context.CounterAudits.ExecuteDeleteAsync(cancellationToken);
        await context.CounterTotals.ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Wraps whichever bundle dispatcher is registered. Armed, it ends the process the moment it is
/// called — the events are committed, nothing has been published or nudged. That is the window T1 is
/// about, hit deterministically instead of by luck.
/// </summary>
public sealed class KillingBundleDispatcher(IEventBundleOutboxDispatcher inner, KillSwitch killSwitch) : IEventBundleOutboxDispatcher
{
    public static void Wrap(IServiceCollection services)
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(IEventBundleOutboxDispatcher));
        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(IEventBundleOutboxDispatcher),
            sp => new KillingBundleDispatcher(CreateInner(sp, descriptor), sp.GetRequiredService<KillSwitch>()),
            descriptor.Lifetime));
    }

    private static IEventBundleOutboxDispatcher CreateInner(IServiceProvider services, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationFactory is not null)
        {
            return (IEventBundleOutboxDispatcher)descriptor.ImplementationFactory(services);
        }

        if (descriptor.ImplementationInstance is not null)
        {
            return (IEventBundleOutboxDispatcher)descriptor.ImplementationInstance;
        }

        return (IEventBundleOutboxDispatcher)ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!);
    }

    public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default)
    {
        if (killSwitch.Armed)
        {
            Console.Out.Flush();
            Environment.FailFast("poc: process ended between commit and publish, as armed");
        }

        return inner.EnqueueEventBundleAsync(eventBundle, cancellationToken);
    }

    public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) =>
        inner.EnqueueOutboxEntriesAsync(outboxEntries, cancellationToken);
}

/// <summary>A read context on a connection string of its own, since the framework's registration reads only <c>defaultdb</c>.</summary>
internal static class ReadStoreRegistration
{
    public static IServiceCollection AddNpgsqlReadDbContextFactoryOn<TContext>(this IServiceCollection services, string connectionString)
        where TContext : DbContext
    {
        services.AddDbContextFactory<TContext>(options => options
            .UseSnakeCaseNamingConvention()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
        return services;
    }
}
