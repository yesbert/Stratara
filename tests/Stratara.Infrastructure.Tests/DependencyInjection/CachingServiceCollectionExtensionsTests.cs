using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

public class CachingServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCaching_MissingConnectionString_Throws()
    {
        var builder = Host.CreateApplicationBuilder();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddCaching());

        Assert.Contains("redis", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddCaching_WithConnectionString_RegistersConnectionMultiplexerSingleton()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = "localhost:6379,connectTimeout=10,abortConnect=false",
        });

        builder.AddCaching();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IConnectionMultiplexer));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    [Fact]
    public void AddCaching_ReturnsSameBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = "localhost:6379,abortConnect=false",
        });

        var result = builder.AddCaching();

        Assert.Same(builder, result);
    }
}
