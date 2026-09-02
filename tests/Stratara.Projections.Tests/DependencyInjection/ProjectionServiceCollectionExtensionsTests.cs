using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;

namespace Stratara.Projections.Tests.DependencyInjection;

public class ProjectionServiceCollectionExtensionsTests
{
    public sealed record FakeProjectionEvent(string Payload);

    public sealed class FirstProjection : IProjection
    {
        public Task HandleAsync(FakeProjectionEvent @event, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class SecondProjection : IProjection
    {
        private Task HandleAsync(FakeProjectionEvent @event, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public abstract class AbstractProjection : IProjection;

    public interface IUnrelated;

    [Fact]
    public void AddProjectionWorker_RegistersScopedManagerHandlerAndInvoker()
    {
        var services = new ServiceCollection();

        services.AddProjectionWorker(new ConfigurationBuilder().Build());

        AssertSingleScoped<IProjectionManager, ProjectionManager>(services);
        AssertSingleScoped<IProjectionHandler, ProjectionHandler>(services);
        AssertSingleScoped<IProjectionMethodInvoker, ProjectionMethodInvoker>(services);
    }

    [Fact]
    public void AddProjectionWorker_RegistersProjectionWorkerAndReplayWorkerAsHostedServices()
    {
        var services = new ServiceCollection();

        services.AddProjectionWorker(new ConfigurationBuilder().Build());

        Assert.Single(services, d => d.ImplementationType == typeof(ProjectionWorker));
        Assert.Single(services, d => d.ImplementationType == typeof(ProjectionReplayWorker));
    }

    [Fact]
    public void AddProjectionWorker_BindsDegreeOfParallelismFromTheProjectionsSection()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Projections:DegreeOfParallelism"] = "1" })
            .Build();

        services.AddProjectionWorker(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(1, provider.GetRequiredService<IOptions<ProjectionOptions>>().Value.DegreeOfParallelism);
    }

    [Fact]
    public void AddProjectionWorker_LeavesDegreeOfParallelismUnsetByDefault()
    {
        var services = new ServiceCollection();

        services.AddProjectionWorker(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetRequiredService<IOptions<ProjectionOptions>>().Value.DegreeOfParallelism);
    }

    [Fact]
    public void AddProjectionsFromAssemblyContaining_RegistersConcreteProjectionsAsScoped()
    {
        var services = new ServiceCollection();

        services.AddProjectionsFromAssemblyContaining<FirstProjection>();

        var projectionDescriptors = services.Where(d => d.ServiceType == typeof(IProjection)).ToList();
        Assert.Contains(projectionDescriptors, d => d.ImplementationType == typeof(FirstProjection));
        Assert.Contains(projectionDescriptors, d => d.ImplementationType == typeof(SecondProjection));
        Assert.All(projectionDescriptors, d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }

    [Fact]
    public void AddProjectionsFromAssemblyContaining_SkipsAbstractAndInterfaceTypes()
    {
        var services = new ServiceCollection();

        services.AddProjectionsFromAssemblyContaining<FirstProjection>();

        var projectionDescriptors = services.Where(d => d.ServiceType == typeof(IProjection)).ToList();
        Assert.DoesNotContain(projectionDescriptors, d => d.ImplementationType == typeof(AbstractProjection));
        Assert.DoesNotContain(projectionDescriptors, d => d.ImplementationType == typeof(IUnrelated));
    }

    private static void AssertSingleScoped<TService, TImpl>(IServiceCollection services)
    {
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TService));
        Assert.Equal(typeof(TImpl), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
}
