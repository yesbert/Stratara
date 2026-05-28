using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Stratara.ServiceDefaults.Tests;

public class SerilogExtensionsTests
{
    [Fact]
    public void ConfigureSerilog_RegistersSerilogILoggerService()
    {
        var builder = NewBuilder();

        builder.ConfigureSerilog();

        var sp = builder.Services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<ILogger>());
    }

    [Fact]
    public void ConfigureSerilog_RegistersAtLeastOneLoggerProvider()
    {
        var builder = NewBuilder();

        builder.ConfigureSerilog();

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(ILoggerProvider));
    }

    [Fact]
    public void ConfigureSerilog_ReturnsSameBuilderForChaining()
    {
        var builder = NewBuilder();

        var returned = builder.ConfigureSerilog();

        Assert.Same(builder, returned);
    }

    [Fact]
    public void ConfigureSerilogBootstrapLogger_ReplacesGlobalLogLogger()
    {
        var previous = Log.Logger;
        try
        {
            ILogger placeholder = new LoggerConfiguration().CreateLogger();
            placeholder.ConfigureSerilogBootstrapLogger();

            Assert.NotNull(Log.Logger);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    private static IHostApplicationBuilder NewBuilder() =>
        Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
}
