using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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

    [Fact]
    public async Task CreateProjectionReplayBatchPipeline_RetriesAnyExceptionAndReturnsTheEventualResult()
    {
        var clock = new FakeTimeProvider();
        var pipeline = ProjectionReplayBatchPipelineOn(clock);

        var attempts = 0;
        var execution = pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            return attempts < 3
                ? throw new TimeoutException("read store busy")
                : ValueTask.FromResult(42);
        }).AsTask();

        await PumpAsync(clock, () => execution.IsCompleted);

        Assert.Equal(42, await execution);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task CreateProjectionReplayBatchPipeline_StopsAfterFiveAttemptsAndSurfacesTheLastException()
    {
        var clock = new FakeTimeProvider();
        var pipeline = ProjectionReplayBatchPipelineOn(clock);

        var attempts = 0;
        var execution = pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            throw new InvalidOperationException($"attempt {attempts}");
        }).AsTask();

        await PumpAsync(clock, () => execution.IsCompleted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        Assert.Equal(ResilienceFactory.ProjectionReplayBatchAttempts, attempts);
        Assert.Equal($"attempt {ResilienceFactory.ProjectionReplayBatchAttempts}", ex.Message);
    }

    [Fact]
    public async Task CreateProjectionReplayBatchPipeline_DoesNotRetryCancellation()
    {
        var builder = new ResiliencePipelineBuilder();
        ResilienceFactory.CreateProjectionReplayBatchPipeline(builder);
        var pipeline = builder.Build();

        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync(_ =>
            {
                attempts++;
                throw new OperationCanceledException();
            }));

        Assert.Equal(1, attempts);
    }

    private static ResiliencePipeline ProjectionReplayBatchPipelineOn(FakeTimeProvider clock)
    {
        var builder = new ResiliencePipelineBuilder { TimeProvider = clock };
        ResilienceFactory.CreateProjectionReplayBatchPipeline(builder);
        return builder.Build();
    }

    private static async Task PumpAsync(FakeTimeProvider clock, Func<bool> until, int seconds = 600)
    {
        for (var i = 0; i < seconds && !until(); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
    }
}
