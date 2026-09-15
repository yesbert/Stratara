using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>
/// Today's shape for comparison: the bus-fed command worker in one host, dispatching through the
/// bus-backed outbox dispatcher, with the same probe command and table as the intent host. Commands:
/// <c>load count ratePerSecond</c>, <c>applied-count</c>.
/// </summary>
public sealed class BusScenario : IPocScenario
{
    public async Task<IHost> BuildAsync(PocHostSettings settings)
    {
        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = settings.StoreConnectionString,
            ["ConnectionStrings:rabbitmq"] = settings.RabbitConnectionString,
        });
        builder.AddCommandWorkerServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<RecordApplied>, RecordAppliedHandler>()
            .AddAggregatesFromAssemblyContaining<BusScenario>()
            .AddTrustedType<RecordApplied>()
            .AddSingleton(new AppliedTable(settings.StoreConnectionString));

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        await AppliedTable.EnsureSchemaAsync(settings.StoreConnectionString);
        return host;
    }

    public async Task<string> HandleAsync(IServiceProvider services, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "applied-count":
                return (await services.GetRequiredService<AppliedTable>().AppliedCountAsync()).ToString(CultureInfo.InvariantCulture);
            case "load":
                LoadGenerator.Start(services, int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
                return "ok";
            default:
                return "error unknown command " + parts[0];
        }
    }
}
