using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;
using Stratara.Abstractions.EventSourcing;
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

    [Fact]
    public async Task CreatePrecedingFactPipeline_RetriesAMissingPrecedingFact()
    {
        var builder = new ResiliencePipelineBuilder();
        ResilienceFactory.CreatePrecedingFactPipeline(builder);
        var pipeline = builder.Build();

        var attempts = 0;
        await pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            return attempts < 2
                ? throw new PrecedingFactMissingException(Guid.NewGuid(), "Updated")
                : ValueTask.CompletedTask;
        });

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CreatePrecedingFactPipeline_DoesNotRetryOtherExceptions()
    {
        var builder = new ResiliencePipelineBuilder();
        ResilienceFactory.CreatePrecedingFactPipeline(builder);
        var pipeline = builder.Build();

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void AddResiliencePipelines_ResolvesAllFiveNamedPolicies()
    {
        var services = new ServiceCollection();
        services.AddResiliencePipelines();
        services.AddResiliencePipelines();
        using var provider = services.BuildServiceProvider();
        var pipelines = provider.GetRequiredService<ResiliencePipelineProvider<string>>();

        foreach (var name in new[]
                 {
                     ResilienceNames.MessageBus, ResilienceNames.CommandDispatcher, ResilienceNames.EventBundleDispatcher,
                     ResilienceNames.ConcurrencyConflict, ResilienceNames.PrecedingFact,
                 })
        {
            Assert.NotNull(pipelines.GetPipeline(name));
        }

        Assert.NotSame(pipelines.GetPipeline(ResilienceNames.PrecedingFact), pipelines.GetPipeline(ResilienceNames.ConcurrencyConflict));
    }
}
