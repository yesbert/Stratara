using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Stratara.Mediator.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Authorization;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

public class AuthorizationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAuthorizingMediator_RegistersAuthorizationProvider()
    {
        var services = new ServiceCollection();
        services.AddAuthorizingMediator<TestAuthorizationProvider>();

        var descriptor = services.Single(d => d.ServiceType == typeof(IAuthorizationProvider));
        Assert.Equal(typeof(TestAuthorizationProvider), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddAuthorizingMediator_RegistersIMediator_AsAuthorizingDecorator()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Tracer>(TracerProvider.Default.GetTracer("test"));
        services.AddAuthorizingMediator<TestAuthorizationProvider>();

        var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        Assert.IsType<AuthorizingMediator>(mediator);
    }

    [Fact]
    public void AddAuthorizingMediator_BothMediatorRegistrations_ScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddAuthorizingMediator<TestAuthorizationProvider>();

        var inner = services.Single(d => d.ServiceType == typeof(Stratara.Mediator.Mediator));
        var outer = services.Single(d => d.ServiceType == typeof(IMediator));
        Assert.Equal(ServiceLifetime.Scoped, inner.Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, outer.Lifetime);
    }

    [Fact]
    public void AddAuthorizingCommandOutboxDispatcher_RegistersDecorator_AsScoped()
    {
        var services = new ServiceCollection();
        services.AddAuthorizingCommandOutboxDispatcher();

        var descriptor = services.Single(d => d.ServiceType == typeof(ICommandOutboxDispatcher));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddAuthorizingCommandOutboxDispatcher_RegistersConcreteInner_AsScoped()
    {
        var services = new ServiceCollection();
        services.AddAuthorizingCommandOutboxDispatcher();

        var inner = services.Single(d => d.ServiceType == typeof(Stratara.Outbox.RabbitMQ.Outbox.CommandOutboxDispatcher));
        Assert.Equal(ServiceLifetime.Scoped, inner.Lifetime);
    }

    private sealed class TestAuthorizationProvider : IAuthorizationProvider
    {
        public Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
