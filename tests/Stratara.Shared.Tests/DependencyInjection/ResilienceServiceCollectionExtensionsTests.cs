using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly.Registry;
using Stratara.Resilience;

namespace Stratara.Shared.Tests.DependencyInjection;

public class ResilienceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddResiliencePipelines_RegistersAll_NamedPipelines()
    {
        var services = new ServiceCollection();
        services.AddResiliencePipelines();

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();

        Assert.NotNull(provider.GetPipeline(ResilienceNames.MessageBus));
        Assert.NotNull(provider.GetPipeline(ResilienceNames.CommandDispatcher));
        Assert.NotNull(provider.GetPipeline(ResilienceNames.EventBundleDispatcher));
    }

    [Fact]
    public void AddResiliencePipelines_PipelinesAreDistinctInstances()
    {
        var services = new ServiceCollection();
        services.AddResiliencePipelines();

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();

        var bus = provider.GetPipeline(ResilienceNames.MessageBus);
        var command = provider.GetPipeline(ResilienceNames.CommandDispatcher);
        var bundle = provider.GetPipeline(ResilienceNames.EventBundleDispatcher);

        Assert.NotSame(bus, command);
        Assert.NotSame(command, bundle);
        Assert.NotSame(bus, bundle);
    }

    [Fact]
    public void AddResiliencePipelines_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddResiliencePipelines();
        services.AddResiliencePipelines();

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();

        Assert.NotNull(provider.GetPipeline(ResilienceNames.MessageBus));
    }

    [Fact]
    public void ResilienceNames_ConstantsAreStable()
    {
        Assert.Equal("MessageBusPipeline", ResilienceNames.MessageBus);
        Assert.Equal("CommandDispatcherPipeline", ResilienceNames.CommandDispatcher);
        Assert.Equal("EventBundleDispatcherPipeline", ResilienceNames.EventBundleDispatcher);
    }
}
