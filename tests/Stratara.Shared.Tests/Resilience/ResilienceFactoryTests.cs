using Polly;
using Stratara.Resilience;

namespace Stratara.Shared.Tests.Resilience;

public class ResilienceFactoryTests
{
    [Fact]
    public async Task CreateMessageBusPipeline_ExecutesSuccessfully()
    {
        var builder = new ResiliencePipelineBuilder();
        ResilienceFactory.CreateMessageBusPipeline(builder);
        var pipeline = builder.Build();

        var executed = false;
        await pipeline.ExecuteAsync(_ =>
        {
            executed = true;
            return ValueTask.CompletedTask;
        });

        Assert.True(executed);
    }

    [Fact]
    public async Task CreateCommandDispatcherPipeline_ExecutesSuccessfully()
    {
        var builder = new ResiliencePipelineBuilder();
        ResilienceFactory.CreateCommandDispatcherPipeline(builder);
        var pipeline = builder.Build();

        var executed = false;
        await pipeline.ExecuteAsync(_ =>
        {
            executed = true;
            return ValueTask.CompletedTask;
        });

        Assert.True(executed);
    }

    [Fact]
    public async Task CreateEventBundleDispatcherPipeline_ExecutesSuccessfully()
    {
        var builder = new ResiliencePipelineBuilder();
        ResilienceFactory.CreateEventBundleDispatcherPipeline(builder);
        var pipeline = builder.Build();

        var executed = false;
        await pipeline.ExecuteAsync(_ =>
        {
            executed = true;
            return ValueTask.CompletedTask;
        });

        Assert.True(executed);
    }
}
