using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>
/// A silo of no role, joined to the cluster the test names, with the heavy-work settings of <see cref="HeavyScenario"/>:
/// started alone, the first call to the permit grain activates the grain on it, so a test can kill the silo keeping the
/// permits. Commands: <c>in-use</c>.
/// </summary>
public sealed class PermitKeeperScenario : IPocScenario
{
    public async Task<IHost> BuildAsync(PocHostSettings settings)
    {
        await PocSilo.EnsureSchemaAsync(settings.OrleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.UseOrleans(silo => settings.ConfigureSilo(silo));
        builder.Services.ConfigureStrataraHeavyWork(options =>
        {
            options.ClusterWideLimit = HeavyScenario.ClusterWideLimit;
            options.PermitLease = HeavyScenario.PermitLease;
        });

        return builder.Build();
    }

    public async Task<string> HandleAsync(IServiceProvider services, string command) => command switch
    {
        "in-use" => (await services.GetRequiredService<IGrainFactory>().GetGrain<IHeavyWorkPermitGrain>(0).InUseAsync()).ToString(CultureInfo.InvariantCulture),
        _ => "error unknown command " + command,
    };
}
