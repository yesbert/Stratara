using Microsoft.Extensions.DependencyInjection;
using Moq;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Sagas;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The store readers a host registers name the consumers they checkpoint under: every registered
/// projection once, and every registered saga once under its own consumer only when saga grains are registered, with
/// the consumer the sagas shared before as the one they superseded.
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
    public async Task Saga_grains_add_a_consumer_per_saga()
    {
        var names = await ConsumerNamesAsync(services => services.AddStrataraProjectionGrains().AddStrataraSagaGrains());

        Assert.Equal(["Orders", "Totals", "sagas:BillingSaga", "sagas:EmailSaga"], names);
    }

    [Fact]
    public async Task Saga_grains_name_the_shared_saga_consumer_as_superseded()
    {
        var superseded = await ConsumerNamesAsync(
            services => services.AddStrataraProjectionGrains().AddStrataraSagaGrains(),
            target => target.SupersededConsumerNames);

        Assert.Equal([SagaGrain.ConsumerName], superseded);
    }

    private static async Task<List<string>> ConsumerNamesAsync(Action<IServiceCollection> register, Func<INudgeTarget, IReadOnlyList<string>>? names = null)
    {
        names ??= target => target.ConsumerNames;
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
            .AddScoped(_ => totalsAgain)
            .AddScoped<ISaga, BillingSaga>()
            .AddScoped<ISaga, EmailSaga>()
            .AddScoped<ISaga>(_ => new EmailSaga());
        register(services);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return [.. scope.ServiceProvider.GetServices<INudgeTarget>().SelectMany(names).Order(StringComparer.Ordinal)];
    }

    private sealed class BillingSaga : ISaga;

    private sealed class EmailSaga : ISaga;
}
