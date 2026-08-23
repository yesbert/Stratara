using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;
using Stratara.Abstractions.Persistence;
using Stratara.Resilience;
using Xunit;

namespace Stratara.Shared.Tests.DependencyInjection;

/// <summary>
/// The attempt bounds in the resilience spec rested on reading the factory: the existing coverage
/// asserts the pipelines are registered and non-null, which passes whatever the retry counts are.
/// These run the failure path and count.
/// </summary>
public class ResiliencePipelineFailurePathTests
{
    private static ResiliencePipeline Pipeline(string name)
    {
        var services = new ServiceCollection();
        services.AddResiliencePipelines();
        return services.BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>()
            .GetPipeline(name);
    }

    private static async Task<int> AttemptsUntilItGivesUpAsync(ResiliencePipeline pipeline, Func<Exception> failure)
    {
        var attempts = 0;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await pipeline.ExecuteAsync(_ =>
            {
                attempts++;
                throw failure();
            }));

        return attempts;
    }

    [Theory]
    [InlineData(ResilienceNames.CommandDispatcher)]
    [InlineData(ResilienceNames.EventBundleDispatcher)]
    public async Task ADispatcherPipelineGivesUpAfterFourAttempts(string name)
    {
        var attempts = await AttemptsUntilItGivesUpAsync(
            Pipeline(name), () => new InvalidOperationException("permanent"));

        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task TheConcurrencyPipelineGivesUpAfterSixAttempts()
    {
        var attempts = await AttemptsUntilItGivesUpAsync(
            Pipeline(ResilienceNames.ConcurrencyConflict), () => new ConcurrencyConflictException());

        Assert.Equal(6, attempts);
    }

    [Fact]
    public async Task TheConcurrencyPipelineDoesNotRetryAnythingElse()
    {
        var attempts = await AttemptsUntilItGivesUpAsync(
            Pipeline(ResilienceNames.ConcurrencyConflict), () => new InvalidOperationException("not a conflict"));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ADispatcherPipelineStopsRetryingOnceItSucceeds()
    {
        var attempts = 0;

        await Pipeline(ResilienceNames.CommandDispatcher).ExecuteAsync(_ =>
        {
            attempts++;
            if (attempts < 2)
            {
                throw new InvalidOperationException("transient");
            }

            return ValueTask.CompletedTask;
        });

        Assert.Equal(2, attempts);
    }
}
