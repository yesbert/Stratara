using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Mediator;
using Stratara.Outbox.RabbitMQ.Mediator;
using Stratara.Outbox.RabbitMQ.Messaging;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Outbox.RabbitMQ.Projections;
using Stratara.Sagas.Services;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Shared.EventSourcing;

namespace Stratara.EventSourcing.WorkerDefaults.Tests;

public class WorkerDefaultsCompositesTests
{
    [Fact]
    public void AddBackendServices_RegistersMediatorAndOutboxDispatcher()
    {
        var builder = NewBuilder();

        builder.AddBackendServices();

        AssertRegistered<IMediator>(builder);
        AssertRegistered<ICommandOutboxDispatcher>(builder);
        AssertRegistered<IEventBundleOutboxDispatcher>(builder);
        AssertRegistered<IMessageBus>(builder);
        AssertRegistered<ISessionContextProvider>(builder);
        AssertBoundOptions<EventSourcingOptions>(builder);
    }

    [Fact]
    public void AddCommandWorkerServices_RegistersMediatorWorkerHostedService()
    {
        var builder = NewBuilder();

        builder.AddCommandWorkerServices();

        Assert.Contains(builder.Services, d => d.ImplementationType == typeof(MediatorCommandWorker));
        AssertRegistered<IMediator>(builder);
        AssertRegistered<ICommandOutboxDispatcher>(builder);
    }

    [Fact]
    public void AddEventProjectionWorkerServices_RegistersProjectionReplayStateAndWorker()
    {
        var builder = NewBuilder();

        builder.AddEventProjectionWorkerServices();

        AssertRegistered<IProjectionReplayState>(builder);
        Assert.Contains(builder.Services, d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType?.Name.Contains("Projection", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void AddSagaWorkerServices_RegistersSagaWorkerHostedService()
    {
        var builder = NewBuilder();

        builder.AddSagaWorkerServices();

        Assert.Contains(builder.Services, d => d.ImplementationType == typeof(SagaWorker));
        AssertRegistered<ICommandOutboxDispatcher>(builder);
    }

    [Fact]
    public void AddEventStreamHashWorkerServices_RegistersEventStreamHashWorker()
    {
        var builder = NewBuilder();

        builder.AddEventStreamHashWorkerServices();

        Assert.Contains(builder.Services, d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType?.Name == "EventStreamHashWorker");
    }

    [Fact]
    public void AddOutboxWorkerServices_RegistersOutboxWorkerHostedService()
    {
        var builder = NewBuilder();

        builder.AddOutboxWorkerServices();

        Assert.Contains(builder.Services, d => d.ImplementationType == typeof(OutboxWorker));
        AssertRegistered<ICommandOutboxDispatcher>(builder);
    }

    [Fact]
    public void AddBackendServices_ReturnsSameBuilderForChaining()
    {
        var builder = NewBuilder();

        var returned = builder.AddBackendServices();

        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddCommonFrameworkServices_IsAppliedByEveryComposite()
    {
        // Indirect proof: each composite ends up with ISessionContextProvider registered, which only
        // comes from the private AddCommonFrameworkServices() call. If a future refactor drops that
        // call from a composite, this catches it.
        foreach (var apply in new Action<IHostApplicationBuilder>[]
                 {
                     b => b.AddBackendServices(),
                     b => b.AddCommandWorkerServices(),
                     b => b.AddEventProjectionWorkerServices(),
                     b => b.AddSagaWorkerServices(),
                     b => b.AddEventStreamHashWorkerServices(),
                     b => b.AddOutboxWorkerServices(),
                 })
        {
            var builder = NewBuilder();
            apply(builder);
            AssertRegistered<ISessionContextProvider>(builder);
        }
    }

    private static IHostApplicationBuilder NewBuilder() =>
        Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });

    private static void AssertRegistered<T>(IHostApplicationBuilder builder) =>
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(T));

    private static void AssertBoundOptions<TOptions>(IHostApplicationBuilder builder) where TOptions : class
    {
        // Options bindings register IConfigureOptions<TOptions> as a service.
        Assert.Contains(builder.Services, d =>
            d.ServiceType.IsGenericType &&
            d.ServiceType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Options.IConfigureOptions<>) &&
            d.ServiceType.GetGenericArguments()[0] == typeof(TOptions));
    }
}
