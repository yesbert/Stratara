using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

public class TestSupportEnvironmentGuardTests
{
    private const string EntryPoint = "AddStrataraTestingEventStore";

    private static string? NoEnvironmentVariables(string name) => null;

    private static Func<string, string?> EnvironmentVariable(string name, string value) =>
        requested => string.Equals(requested, name, StringComparison.Ordinal) ? value : null;

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Stub";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IServiceCollection WithHostEnvironment(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment { EnvironmentName = environmentName });
        return services;
    }

    [Fact]
    public void Registers_WhenNoEnvironmentIsStatedAnywhere()
    {
        var services = new ServiceCollection();

        var ex = Record.Exception(() =>
            TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(services, EntryPoint, NoEnvironmentVariables));

        Assert.Null(ex);
    }

    [Fact]
    public void Registers_WhenTheHostSaysDevelopment()
    {
        var services = WithHostEnvironment(Environments.Development);

        var ex = Record.Exception(() =>
            TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(services, EntryPoint, NoEnvironmentVariables));

        Assert.Null(ex);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    [InlineData("a-name-the-framework-has-never-heard-of")]
    public void Refuses_WhenTheHostSaysAnythingElse(string environmentName)
    {
        var services = WithHostEnvironment(environmentName);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(services, EntryPoint, NoEnvironmentVariables));

        Assert.Contains(environmentName, ex.Message, StringComparison.Ordinal);
        Assert.Contains(EntryPoint, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DOTNET_ENVIRONMENT")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    public void Refuses_WhenTheEnvironmentVariableSaysProduction(string variable)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(
                services, EntryPoint, EnvironmentVariable(variable, "Production")));

        Assert.Contains("Production", ex.Message, StringComparison.Ordinal);
        Assert.Contains(variable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registers_WhenTheEnvironmentVariableSaysDevelopment()
    {
        var services = new ServiceCollection();

        var ex = Record.Exception(() =>
            TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(
                services, EntryPoint, EnvironmentVariable("DOTNET_ENVIRONMENT", "Development")));

        Assert.Null(ex);
    }

    [Fact]
    public void TheHostWins_OverTheEnvironmentVariable()
    {
        var services = WithHostEnvironment(Environments.Development);

        var ex = Record.Exception(() =>
            TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(
                services, EntryPoint, EnvironmentVariable("DOTNET_ENVIRONMENT", "Production")));

        Assert.Null(ex);
    }
}
