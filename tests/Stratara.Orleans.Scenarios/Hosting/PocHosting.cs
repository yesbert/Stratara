using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>The host builder every scenario starts from: Development, and a log that shows failures rather than every query.</summary>
public static class PocHosting
{
    public static HostApplicationBuilder CreateBuilder()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Orleans", LogLevel.Warning);
        builder.Logging.AddFilter("Polly", LogLevel.Warning);
        return builder;
    }
}
