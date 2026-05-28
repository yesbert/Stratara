using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Messaging;
using Stratara.Outbox.RabbitMQ.Messaging;

namespace Stratara.Outbox.RabbitMQ.Tests.Messaging;

/// <summary>
/// Regression-anchor for the v3.0.14 RabbitMqBus production-fail-fast guard. The bus must throw
/// <see cref="InvalidOperationException"/> rather than silently fall back to <c>guest/guest</c>
/// when a Production host is misconfigured (RABBITMQ_HOST set, but RABBITMQ_USERNAME or
/// RABBITMQ_PASSWORD missing).
/// </summary>
public sealed class RabbitMqBusProductionGuardTests
{
    [Fact]
    public async Task PublishAsync_ProductionEnvironment_MissingCredentials_ThrowsInvalidOperationException()
    {
        var config = BuildConfiguration(host: "rabbit.example.com", username: null, password: null);
        var prodEnv = MockEnvironment(Environments.Production);
        await using var bus = CreateBus(config, prodEnv);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.PublishAsync("test-topic", new { Payload = "x" }, CancellationToken.None));

        Assert.Contains("Production", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RABBITMQ_USERNAME", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RABBITMQ_PASSWORD", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAsync_ProductionEnvironment_MissingUsernameOnly_ThrowsInvalidOperationException()
    {
        var config = BuildConfiguration(host: "rabbit.example.com", username: null, password: "secret");
        var prodEnv = MockEnvironment(Environments.Production);
        await using var bus = CreateBus(config, prodEnv);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.PublishAsync("test-topic", new { Payload = "x" }, CancellationToken.None));
    }

    [Fact]
    public async Task PublishAsync_ProductionEnvironment_MissingPasswordOnly_ThrowsInvalidOperationException()
    {
        var config = BuildConfiguration(host: "rabbit.example.com", username: "user", password: null);
        var prodEnv = MockEnvironment(Environments.Production);
        await using var bus = CreateBus(config, prodEnv);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.PublishAsync("test-topic", new { Payload = "x" }, CancellationToken.None));
    }

    [Fact]
    public async Task PublishAsync_DevelopmentEnvironment_MissingCredentials_DoesNotThrowInvalidOperation()
    {
        var config = BuildConfiguration(host: "rabbit.example.com", username: null, password: null);
        var devEnv = MockEnvironment(Environments.Development);
        await using var bus = CreateBus(config, devEnv);

        var exception = await Record.ExceptionAsync(
            () => bus.PublishAsync("test-topic", new { Payload = "x" }, CancellationToken.None));

        Assert.NotNull(exception);
        Assert.IsNotType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task PublishAsync_StagingEnvironment_MissingCredentials_DoesNotThrowInvalidOperation()
    {
        var config = BuildConfiguration(host: "rabbit.example.com", username: null, password: null);
        var stagingEnv = MockEnvironment("Staging");
        await using var bus = CreateBus(config, stagingEnv);

        var exception = await Record.ExceptionAsync(
            () => bus.PublishAsync("test-topic", new { Payload = "x" }, CancellationToken.None));

        Assert.NotNull(exception);
        Assert.IsNotType<InvalidOperationException>(exception);
    }

    private static IConfiguration BuildConfiguration(string? host, string? username, string? password)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["RABBITMQ_HOST"] = host,
            ["RABBITMQ_USERNAME"] = username,
            ["RABBITMQ_PASSWORD"] = password,
        };
        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    private static IHostEnvironment MockEnvironment(string environmentName)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return env.Object;
    }

    private static RabbitMqBus CreateBus(IConfiguration config, IHostEnvironment env) =>
        new(NullLogger<RabbitMqBus>.Instance, config, env, Options.Create(new BusEnvelopeJsonOptions()));
}
