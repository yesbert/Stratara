using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Task 2.1 of optimise-the-orleans-execution-model: the rebuilder resumes every partition at once.
/// The grains here complete a resume only once every partition has been asked to resume, so a
/// rebuilder that resumed them one after another would never finish.
/// </summary>
public sealed class ProjectionRebuilderTests
{
    private const int Partitions = 16;

    [Fact]
    public async Task Rebuild_resumes_every_partition_concurrently()
    {
        var allResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = 0;
        var paused = 0;
        var grains = new Dictionary<string, IProjectionGrain>();
        for (var partition = 0; partition < Partitions; partition++)
        {
            grains[StoreReaderGrainKey.Of("View", partition)] = new FakeGrain(
                () => Interlocked.Increment(ref paused),
                () =>
                {
                    if (Interlocked.Increment(ref resumed) == Partitions)
                    {
                        allResumed.TrySetResult();
                    }

                    return allResumed.Task;
                });
        }

        var grainFactory = new Mock<IGrainFactory>();
        grainFactory
            .Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null))
            .Returns((string key, string? _) => grains[key]);

        var projection = new Mock<IRebuildableProjection>();
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(projection.Object)).Returns("View");
        var checkpoints = new Mock<IProjectionCheckpointStore>();
        var reader = new Mock<ICommittedPositionReader>();

        var services = new ServiceCollection()
            .AddScoped(_ => handler.Object)
            .AddScoped<IProjection>(_ => projection.Object)
            .AddScoped(_ => checkpoints.Object)
            .AddScoped(_ => reader.Object)
            .BuildServiceProvider();

        var rebuilder = new ProjectionRebuilder(
            grainFactory.Object,
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CommitOrderOptions { PartitionCount = Partitions }));

        var rebuild = rebuilder.RebuildAsync("View");
        var finished = await Task.WhenAny(rebuild, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(ReferenceEquals(finished, rebuild), "the rebuilder resumed the partitions one after another");
        await rebuild;
        Assert.Equal(Partitions, paused);
        Assert.Equal(Partitions, resumed);
        projection.Verify(p => p.TruncateAsync(It.IsAny<CancellationToken>()), Times.Once);
        checkpoints.Verify(c => c.SetAsync("View", It.IsAny<int>(), It.IsAny<string>(), 0, It.IsAny<CancellationToken>()), Times.Exactly(Partitions));
    }

    /// <summary>The grain interface is internal, which a proxy generator cannot reach; a fake can.</summary>
    private sealed class FakeGrain(Action onPause, Func<Task> onResume) : IProjectionGrain
    {
        public Task EnsureRunningAsync() => Task.CompletedTask;

        public Task NudgeAsync() => Task.CompletedTask;

        public Task<int> CatchUpAsync() => Task.FromResult(0);

        public Task<long> PositionAsync() => Task.FromResult(0L);

        public Task PauseAsync()
        {
            onPause();
            return Task.CompletedTask;
        }

        public Task ResumeAsync() => onResume();
    }
}
