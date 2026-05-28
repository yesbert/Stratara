using Testcontainers.ServiceBus;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;

/// <summary>
/// xUnit collection fixture that boots a single Microsoft Service Bus emulator container per test
/// collection. Exposes the connection string so tests can construct an
/// <c>Azure.Messaging.ServiceBus.ServiceBusClient</c> and the <c>Stratara.Outbox.AzureServiceBus.Messaging.AzureServiceBusBus</c>
/// wrapper.
/// </summary>
/// <remarks>
/// The Azure Service Bus emulator does not support runtime topic / subscription creation — the set
/// of topics and subscriptions must be predeclared in a <c>Config.json</c> mounted into the container.
/// This fixture mounts <c>Fixtures/servicebus-emulator-config.json</c>, which declares the five
/// topic / subscription pairs the integration tests in <c>ServiceBusTests</c> use.
/// </remarks>
public sealed class ServiceBusFixture : IAsyncLifetime
{
    private readonly ServiceBusContainer _container = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
        .WithAcceptLicenseAgreement(true)
        .WithConfig(Path.Combine(AppContext.BaseDirectory, "Fixtures", "servicebus-emulator-config.json"))
        .Build();

    /// <summary>Connection string to the running Service Bus emulator (set after <see cref="InitializeAsync"/>).</summary>
    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ServiceBusCollection : ICollectionFixture<ServiceBusFixture>
{
    public const string Name = nameof(ServiceBusCollection);
}
