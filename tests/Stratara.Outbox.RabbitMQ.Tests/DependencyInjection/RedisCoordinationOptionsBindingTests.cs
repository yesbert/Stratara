using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Stratara.Outbox.RabbitMQ.Tests.DependencyInjection;

public class RedisCoordinationOptionsBindingTests
{
    private static HostApplicationBuilder HostWithName(string? productName)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["RedisCoordination:ProductName"] = productName });
        return builder;
    }

    private static RedisCoordinationOptions Resolve(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RedisCoordinationOptions>>().Value;
    }

    [Fact]
    public void AddProjectionReplayState_ReadsTheSection()
    {
        var builder = HostWithName("acme");

        builder.Services.AddProjectionReplayState();

        Assert.Equal("acme", Resolve(builder.Services).ProductName);
    }

    [Fact]
    public void AddRedisOutboxLock_ReadsTheSection()
    {
        var builder = HostWithName("acme");

        builder.Services.AddRedisOutboxLock();

        Assert.Equal("acme", Resolve(builder.Services).ProductName);
    }

    [Fact]
    public void BothRegistrations_BindOnce_SoCodeInBetweenWins()
    {
        var builder = HostWithName("acme");

        builder.Services.AddProjectionReplayState();
        builder.Services.Configure<RedisCoordinationOptions>(o => o.ProductName = "from-code");
        builder.Services.AddRedisOutboxLock();

        Assert.Equal("from-code", Resolve(builder.Services).ProductName);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("   ")]
    [InlineData("a:b")]
    [InlineData("{tag}")]
    [InlineData("umläut")]
    public void AnUnusableName_IsRefusedWhenOptionsAreRead_NamingTheSetting(string productName)
    {
        var builder = HostWithName(productName);

        builder.Services.AddRedisOutboxLock();

        var refused = Assert.Throws<OptionsValidationException>(() => Resolve(builder.Services));
        Assert.Contains("RedisCoordination:ProductName", refused.Message);
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("Acme.Billing_2-eu")]
    [InlineData("")]
    public void AUsableName_IsAccepted(string productName)
    {
        var builder = HostWithName(productName);

        builder.Services.AddRedisOutboxLock();

        Assert.Equal(productName, Resolve(builder.Services).ProductName ?? string.Empty);
    }

    [Theory]
    [InlineData(null, "stratara:outbox:lock", "stratara:outbox:lock")]
    [InlineData("", "stratara:projection:replay:active", "stratara:projection:replay:active")]
    [InlineData("acme", "stratara:outbox:lock", "stratara:acme:outbox:lock")]
    [InlineData("acme", "stratara:projection:replay:claimed:", "stratara:acme:projection:replay:claimed:")]
    public void Names_CarryTheProduct_AndStayFixedWithoutOne(string? productName, string fixedName, string expected)
    {
        Assert.Equal(expected, new RedisCoordinationOptions { ProductName = productName }.Name(fixedName));
    }
}
