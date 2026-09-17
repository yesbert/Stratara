using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Testing.EntityFrameworkCore;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// Scenario <em>The test execution-model host is created under a stated environment</em>, and the build-time check the
/// package ships: the host refuses a stated environment other than development, naming it and what the host runs, and
/// the package's targets fail a project that is not a test project.
/// </summary>
public sealed class ExecutionModelTestHostGuardTests
{
    [Theory]
    [InlineData("DOTNET_ENVIRONMENT")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    public void The_host_refuses_a_stated_environment_other_than_development(string variable)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(
            new ServiceCollection(),
            "ExecutionModelTestHost.CreateAsync",
            TestSupportEnvironmentGuard.ExecutionModelWiring,
            name => name == variable ? "Production" : null));

        Assert.Contains("Production", failure.Message, StringComparison.Ordinal);
        Assert.Contains(variable, failure.Message, StringComparison.Ordinal);
        Assert.Contains("ExecutionModelTestHost.CreateAsync", failure.Message, StringComparison.Ordinal);
        Assert.Contains("execution model in memory", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_is_created_where_no_environment_is_stated()
    {
        var failure = Record.Exception(() => TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(
            new ServiceCollection(), "ExecutionModelTestHost.CreateAsync", TestSupportEnvironmentGuard.ExecutionModelWiring, _ => null));

        Assert.Null(failure);
    }

    [Fact]
    public void The_package_targets_fail_a_project_that_is_not_a_test_project()
    {
        var targets = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Stratara.Testing.Orleans", "build", "Stratara.Testing.Orleans.targets"));
        var error = targets.Descendants("Error").Single();

        Assert.Equal("STRATARA1001", error.Attribute("Code")?.Value);
        Assert.Contains("'$(IsTestProject)' != 'true'", error.Attribute("Condition")?.Value, StringComparison.Ordinal);
        Assert.Contains("StrataraAllowTestSupportOutsideTests", error.Attribute("Condition")?.Value, StringComparison.Ordinal);
        Assert.Contains("Stratara.Testing.Orleans", error.Attribute("Text")?.Value, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stratara.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test's base directory.");
    }
}
