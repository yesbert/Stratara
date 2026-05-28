using Microsoft.Extensions.Configuration;
using Stratara.EventSourcing.EntityFrameworkCore;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests;

public class DefaultDbResolverTests
{
    [Fact]
    public void ResolveConnectionString_WithValidConfig_ReturnsConnectionString()
    {
        var expectedConnectionString = "Host=localhost;Database=testdb";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:defaultdb"] = expectedConnectionString
            })
            .Build();

        var resolver = new DefaultDbResolver(configuration);

        var result = resolver.ResolveConnectionString();

        Assert.Equal(expectedConnectionString, result);
    }

    [Fact]
    public void ResolveConnectionString_WithMissingConfig_ThrowsInvalidOperationException()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var resolver = new DefaultDbResolver(configuration);

        Assert.Throws<InvalidOperationException>(() => resolver.ResolveConnectionString());
    }

    [Fact]
    public void ResolveConnectionString_WithEmptyConfig_ReturnsEmptyString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:defaultdb"] = ""
            })
            .Build();

        var resolver = new DefaultDbResolver(configuration);

        var result = resolver.ResolveConnectionString();

        Assert.Equal("", result);
    }
}
