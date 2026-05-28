using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Mediator;
using Stratara.Outbox.RabbitMQ.Mediator;

namespace Stratara.Outbox.RabbitMQ.Tests.DependencyInjection;

public class MediatorWorkerServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMediatorWorker_RegistersMediatorCommandWorkerAsHostedService()
    {
        var services = new ServiceCollection();

        services.AddMediatorWorker();

        var descriptor = Assert.Single(services, d => d.ImplementationType == typeof(MediatorCommandWorker));
        Assert.Equal(typeof(IHostedService), descriptor.ServiceType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddMediatorWorker_IsIdempotent()
    {
        // AddHostedService uses TryAddEnumerable under the hood, so a second call with
        // the same implementation type collapses into the first registration. This test
        // pins that behaviour so future-us notices if the framework convention shifts.
        var services = new ServiceCollection();

        services.AddMediatorWorker();
        services.AddMediatorWorker();

        var registrations = services.Where(d => d.ImplementationType == typeof(MediatorCommandWorker)).ToList();
        Assert.Single(registrations);
    }
}
