using Microsoft.Extensions.DependencyInjection;
using Moq;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Sagas;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The store readers a host registers name the consumers they checkpoint under: every registered
/// projection once, and the saga consumer only when saga grains are registered.
/// </summary>
public sealed class StoreReaderConsumerNamesTests
{
    [Fact]
    public async Task Projection_grains_name_each_registered_projection_once()
    {
        var names = await ConsumerNamesAsync(services => services.AddStrataraProjectionGrains());

        Assert.Equal(["Orders", "Totals"], names);
    }

    [Fact]
    public async Task Saga_grains_add_the_saga_consumer()
    {
        var names = await ConsumerNamesAsync(services => services.AddStrataraProjectionGrains().AddStrataraSagaGrains());

        Assert.Equal(["Orders", "Totals", SagaGrain.ConsumerName], names);
    }

    private static async Task<List<string>> ConsumerNamesAsync(Action<IServiceCollection> register)
    {
        var orders = new Mock<IProjection>().Object;
        var totals = new Mock<IProjection>().Object;
        var totalsAgain = new Mock<IProjection>().Object;
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(orders)).Returns("Orders");
        handler.Setup(h => h.GetProjectionName(totals)).Returns("Totals");
        handler.Setup(h => h.GetProjectionName(totalsAgain)).Returns("Totals");

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new Mock<IGrainFactory>().Object)
            .AddScoped(_ => handler.Object)
            .AddScoped(_ => orders)
            .AddScoped(_ => totals)
            .AddScoped(_ => totalsAgain);
        register(services);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return [.. scope.ServiceProvider.GetServices<INudgeTarget>().SelectMany(target => target.ConsumerNames).Order(StringComparer.Ordinal)];
    }
}
