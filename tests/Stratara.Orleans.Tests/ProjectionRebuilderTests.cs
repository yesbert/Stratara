using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;

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

    /// <summary>D21: every checkpoint is back at the beginning before the projection is emptied.</summary>
    [Fact]
    public async Task Rebuild_resets_every_checkpoint_before_it_truncates()
    {
        var journal = new List<string>();
        var (rebuilder, projection, checkpoints) = Build(journal);
        projection.Setup(p => p.TruncateAsync(It.IsAny<CancellationToken>())).Callback(() => { lock (journal) { journal.Add("truncate"); } }).Returns(Task.CompletedTask);

        await rebuilder.RebuildAsync("View");

        Assert.Equal(Partitions, journal.IndexOf("truncate"));
        Assert.All(journal.Take(Partitions), entry => Assert.StartsWith("reset", entry, StringComparison.Ordinal));
        checkpoints.Verify(c => c.SetAsync("View", It.IsAny<int>(), "reader/16", 0, It.IsAny<CancellationToken>()), Times.Exactly(Partitions));
    }

    [Fact]
    public async Task A_truncation_that_throws_leaves_the_checkpoints_at_the_beginning_and_resumes_every_partition()
    {
        var journal = new List<string>();
        var (rebuilder, projection, _) = Build(journal);
        projection.Setup(p => p.TruncateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("truncation failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilder.RebuildAsync("View"));

        Assert.Equal(Partitions, journal.Count(entry => entry.StartsWith("reset", StringComparison.Ordinal)));
        Assert.Equal(Partitions, journal.Count(entry => entry == "resume"));
    }

    private static (ProjectionRebuilder Rebuilder, Mock<IRebuildableProjection> Projection, Mock<IProjectionCheckpointStore> Checkpoints) Build(List<string> journal)
    {
        var grains = Enumerable.Range(0, Partitions).ToDictionary(
            partition => StoreReaderGrainKey.Of("View", partition),
            _ => (IProjectionGrain)new FakeGrain(() => { }, () =>
            {
                lock (journal)
                {
                    journal.Add("resume");
                }

                return Task.CompletedTask;
            }));
        var grainFactory = new Mock<IGrainFactory>();
        grainFactory.Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null)).Returns((string key, string? _) => grains[key]);

        var projection = new Mock<IRebuildableProjection>();
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(projection.Object)).Returns("View");
        var checkpoints = new Mock<IProjectionCheckpointStore>();
        checkpoints
            .Setup(c => c.SetAsync("View", It.IsAny<int>(), It.IsAny<string>(), 0, It.IsAny<CancellationToken>()))
            .Callback((string _, int partition, string _, long _, CancellationToken _) => { lock (journal) { journal.Add($"reset {partition}"); } })
            .Returns(Task.CompletedTask);
        var reader = new Mock<ICommittedPositionReader>();
        reader.SetupGet(r => r.Name).Returns("reader/16");

        var services = new ServiceCollection()
            .AddScoped(_ => handler.Object)
            .AddScoped<IProjection>(_ => projection.Object)
            .AddScoped(_ => checkpoints.Object)
            .AddScoped(_ => reader.Object)
            .BuildServiceProvider();

        var rebuilder = new ProjectionRebuilder(grainFactory.Object, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CommitOrderOptions { PartitionCount = Partitions }));
        return (rebuilder, projection, checkpoints);
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
