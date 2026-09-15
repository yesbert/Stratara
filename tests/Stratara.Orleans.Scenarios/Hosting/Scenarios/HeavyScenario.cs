using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.IntegrationTests.HeavyWork;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>
/// A worker silo for heavy work, joined to the cluster the test names, whose units hold their permits
/// for as long as the test asks. Commands: <c>enqueue-heavy count delayMs</c>, <c>in-use</c>.
/// </summary>
public sealed class HeavyScenario : IPocScenario
{
    public const int ClusterWideLimit = 4;
    public static readonly TimeSpan PermitLease = TimeSpan.FromSeconds(4);

    public async Task<IHost> BuildAsync(PocHostSettings settings)
    {
        await PocSilo.EnsureSchemaAsync(settings.OrleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = settings.StoreConnectionString,
            ["ConnectionStrings:rabbitmq"] = settings.RabbitConnectionString,
        });
        builder.UseOrleans(silo => settings.ConfigureSilo(silo));
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<HeavyProbe>, HeavyProbeHandler>()
            .AddAggregatesFromAssemblyContaining<HeavyScenario>()
            .AddTrustedType<HeavyProbe>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocWriteDbContext>()
            .ConfigureStrataraHeavyWork(options =>
            {
                options.ClusterWideLimit = ClusterWideLimit;
                options.PermitLease = PermitLease;
            });

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        return host;
    }

    public async Task<string> HandleAsync(IServiceProvider services, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "enqueue-heavy":
            {
                await using var scope = services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
                var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
                var count = int.Parse(parts[1], CultureInfo.InvariantCulture);
                var delay = int.Parse(parts[2], CultureInfo.InvariantCulture);
                for (var i = 0; i < count; i++)
                {
                    await dispatcher.EnqueueCommandAsync(new HeavyProbe(Guid.NewGuid(), delay));
                }

                return "ok";
            }
            case "in-use":
                return (await services.GetRequiredService<IGrainFactory>().GetGrain<IHeavyWorkPermitGrain>(0).InUseAsync()).ToString(CultureInfo.InvariantCulture);
            default:
                return "error unknown command " + parts[0];
        }
    }
}
