using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Sagas.Abstractions;
using Stratara.Sagas.Services;

namespace Stratara.Sagas.Tests.DependencyInjection;

public class SagaServiceCollectionExtensionsTests
{
    public sealed class FirstSaga : ISaga;

    public sealed class SecondSaga : ISaga;

    public abstract class AbstractSaga : ISaga;

    public interface IUnrelated;

    [Fact]
    public void AddSagaWorker_RegistersScopedManagerHandlerAndInvoker()
    {
        var services = new ServiceCollection();

        services.AddSagaWorker(new ConfigurationBuilder().Build());

        AssertSingleScoped<ISagaManager, SagaManager>(services);
        AssertSingleScoped<ISagaHandler, SagaHandler>(services);
        AssertSingleScoped<ISagaMethodInvoker, SagaMethodInvoker>(services);
    }

    [Fact]
    public void AddSagaWorker_RegistersSagaWorkerAsHostedService()
    {
        var services = new ServiceCollection();

        services.AddSagaWorker(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, d => d.ImplementationType == typeof(SagaWorker));
        Assert.Equal(typeof(IHostedService), descriptor.ServiceType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddSagaWorker_BindsDegreeOfParallelismFromTheSagasSection()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sagas:DegreeOfParallelism"] = "1" })
            .Build();

        services.AddSagaWorker(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(1, provider.GetRequiredService<IOptions<SagaOptions>>().Value.DegreeOfParallelism);
    }

    [Fact]
    public void AddSagaWorker_LeavesDegreeOfParallelismUnsetByDefault()
    {
        var services = new ServiceCollection();

        services.AddSagaWorker(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetRequiredService<IOptions<SagaOptions>>().Value.DegreeOfParallelism);
    }

    [Fact]
    public void AddSagasFromAssemblyContaining_RegistersConcreteSagasAsScoped()
    {
        var services = new ServiceCollection();

        services.AddSagasFromAssemblyContaining<FirstSaga>();

        var sagaDescriptors = services.Where(d => d.ServiceType == typeof(ISaga)).ToList();
        Assert.Contains(sagaDescriptors, d => d.ImplementationType == typeof(FirstSaga));
        Assert.Contains(sagaDescriptors, d => d.ImplementationType == typeof(SecondSaga));
        Assert.All(sagaDescriptors, d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }

    [Fact]
    public void AddSagasFromAssemblyContaining_SkipsAbstractAndInterfaceTypes()
    {
        var services = new ServiceCollection();

        services.AddSagasFromAssemblyContaining<FirstSaga>();

        var sagaDescriptors = services.Where(d => d.ServiceType == typeof(ISaga)).ToList();
        Assert.DoesNotContain(sagaDescriptors, d => d.ImplementationType == typeof(AbstractSaga));
        Assert.DoesNotContain(sagaDescriptors, d => d.ImplementationType == typeof(IUnrelated));
    }

    private static void AssertSingleScoped<TService, TImpl>(IServiceCollection services)
    {
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TService));
        Assert.Equal(typeof(TImpl), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
}
