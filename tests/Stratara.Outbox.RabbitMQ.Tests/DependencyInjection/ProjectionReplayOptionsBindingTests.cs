using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Outbox.RabbitMQ.Projections;

namespace Stratara.Outbox.RabbitMQ.Tests.DependencyInjection;

public class ProjectionReplayOptionsBindingTests
{
    [Fact]
    public void AddProjectionReplayState_ReadsTheProjectionReplaySection()
    {
        var builder = HostWithLease("600");

        builder.Services.AddProjectionReplayState();

        Assert.Equal(600, Resolve(builder.Services).LeaseSeconds);
    }

    [Fact]
    public void AddOutboxDispatcher_ReadsTheProjectionReplaySection()
    {
        var builder = HostWithLease("600");

        builder.Services.AddOutboxDispatcher();

        Assert.Equal(600, Resolve(builder.Services).LeaseSeconds);
    }

    [Fact]
    public void AddProjectionReplayState_CodeAfterTheRegistration_Wins()
    {
        var builder = HostWithLease("600");

        builder.Services.AddProjectionReplayState();
        builder.Services.Configure<ProjectionReplayOptions>(o => o.LeaseSeconds = 900);

        Assert.Equal(900, Resolve(builder.Services).LeaseSeconds);
    }

    [Fact]
    public void AddProjectionReplayState_CalledAgainAfterCode_DoesNotReapplyTheSection()
    {
        var builder = HostWithLease("600");

        builder.Services.AddProjectionReplayState();
        builder.Services.Configure<ProjectionReplayOptions>(o => o.LeaseSeconds = 900);
        builder.Services.AddOutboxDispatcher();

        Assert.Equal(900, Resolve(builder.Services).LeaseSeconds);
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IConfigureOptions<ProjectionReplayOptions>) && d.ImplementationType == typeof(ProjectionReplayOptionsBinding));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IValidateOptions<ProjectionReplayOptions>));
    }

    [Fact]
    public void AddProjectionReplayState_WithoutConfiguration_ResolvesTheDefaults()
    {
        var services = new ServiceCollection();

        services.AddProjectionReplayState();

        Assert.Equal(300, Resolve(services).LeaseSeconds);
    }

    [Fact]
    public async Task AddProjectionReplayState_LeaseOfZeroInTheSection_FailsTheStartNamingTheSetting()
    {
        var builder = HostWithLease("0");
        builder.Services.AddProjectionReplayState();
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ProjectionReplay:LeaseSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddProjectionReplayState_NegativeLeaseInCode_FailsTheStartNamingTheSetting()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddProjectionReplayState();
        builder.Services.Configure<ProjectionReplayOptions>(o => o.LeaseSeconds = -1);
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ProjectionReplay:LeaseSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddProjectionReplayState_RefreshOfZeroInTheSection_FailsTheStartNamingTheSetting()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectionReplay:RefreshSeconds"] = "0",
        });
        builder.Services.AddProjectionReplayState();
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ProjectionReplay:RefreshSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddProjectionReplayState_NegativeRefreshInCode_FailsTheStartNamingTheSetting()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddProjectionReplayState();
        builder.Services.Configure<ProjectionReplayOptions>(o => o.RefreshSeconds = -1);
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ProjectionReplay:RefreshSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddProjectionReplayState_RefreshNotBelowTheLease_FailsTheStartNamingBoth()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddProjectionReplayState();
        builder.Services.Configure<ProjectionReplayOptions>(o =>
        {
            o.LeaseSeconds = 10;
            o.RefreshSeconds = 10;
        });
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ProjectionReplay:RefreshSeconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ProjectionReplay:LeaseSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddProjectionReplayState_RefreshBelowTheLease_StartsTheHost()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddProjectionReplayState();
        builder.Services.Configure<ProjectionReplayOptions>(o =>
        {
            o.LeaseSeconds = 2;
            o.RefreshSeconds = 1;
        });
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, host.Services.GetRequiredService<IOptions<ProjectionReplayOptions>>().Value.RefreshSeconds);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AddProjectionReplayState_DefaultLease_StartsTheHost()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddProjectionReplayState();
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<IOptions<ProjectionReplayOptions>>().Value;
        Assert.Equal(300, options.LeaseSeconds);
        Assert.Equal(5, options.RefreshSeconds);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static HostApplicationBuilder HostWithLease(string leaseSeconds)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectionReplay:LeaseSeconds"] = leaseSeconds,
        });
        return builder;
    }

    private static ProjectionReplayOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<ProjectionReplayOptions>>().Value;
}
