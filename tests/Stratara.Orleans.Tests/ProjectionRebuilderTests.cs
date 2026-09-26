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
            Options.Create(new CommitOrderOptions { PartitionCount = Partitions }),
            NoReplay(),
            Lease());

        var rebuild = rebuilder.RebuildAsync("View");
        var finished = await Task.WhenAny(rebuild, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(ReferenceEquals(finished, rebuild), "the rebuilder resumed the partitions one after another");
        await rebuild;
        Assert.Equal(2 * Partitions, paused);
        Assert.Equal(Partitions, resumed);
        projection.Verify(p => p.TruncateAsync(It.IsAny<CancellationToken>()), Times.Once);
        checkpoints.Verify(c => c.ResetAsync("View", It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2 * Partitions));
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
        checkpoints.Verify(c => c.ResetAsync("View", It.IsAny<int>(), "reader/16", It.IsAny<CancellationToken>()), Times.Exactly(2 * Partitions));
    }

    /// <summary>
    /// Change let-a-paused-reader-always-come-back, D3: the checkpoints return to the beginning again once the projection
    /// is emptied, and only then are the readers resumed — what a reader applied before the truncation although it should
    /// have been paused is read again after it.
    /// </summary>
    [Fact]
    public async Task A_second_reset_follows_the_truncation_and_precedes_the_resume()
    {
        var journal = new List<string>();
        var (rebuilder, projection, _) = Build(journal);
        projection.Setup(p => p.TruncateAsync(It.IsAny<CancellationToken>())).Callback(() => { lock (journal) { journal.Add("truncate"); } }).Returns(Task.CompletedTask);

        await rebuilder.RebuildAsync("View");

        var truncation = journal.IndexOf("truncate");
        var afterTruncation = journal.Skip(truncation + 1).ToList();
        Assert.Equal(
            Enumerable.Range(0, Partitions).Select(partition => $"reset {partition}").Order(StringComparer.Ordinal),
            afterTruncation.Take(Partitions).Order(StringComparer.Ordinal));
        Assert.All(afterTruncation.Skip(Partitions), entry => Assert.Equal("resume", entry));
        Assert.Equal(Partitions, afterTruncation.Count(entry => entry == "resume"));
    }

    /// <summary>
    /// Every reader is paused again once the projection is emptied and before the checkpoints return to the beginning a
    /// second time, so a batch a reader had in flight — begun before the truncation — writes its checkpoint before the
    /// reset rather than over it.
    /// </summary>
    [Fact]
    public async Task The_readers_are_quiesced_between_the_truncation_and_the_second_reset()
    {
        var journal = new List<string>();
        var grains = Enumerable.Range(0, Partitions).ToDictionary(
            partition => StoreReaderGrainKey.Of("View", partition),
            _ => (IProjectionGrain)new FakeGrain(
                () => { lock (journal) { journal.Add("pause"); } },
                () => { lock (journal) { journal.Add("resume"); } return Task.CompletedTask; }));
        var grainFactory = new Mock<IGrainFactory>();
        grainFactory.Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null)).Returns((string key, string? _) => grains[key]);
        var projection = new Mock<IRebuildableProjection>();
        projection.Setup(p => p.TruncateAsync(It.IsAny<CancellationToken>())).Callback(() => { lock (journal) { journal.Add("truncate"); } }).Returns(Task.CompletedTask);
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(projection.Object)).Returns("View");
        var checkpoints = new Mock<IProjectionCheckpointStore>();
        checkpoints
            .Setup(c => c.ResetAsync("View", It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => { lock (journal) { journal.Add("reset"); } })
            .Returns(Task.CompletedTask);
        var reader = new Mock<ICommittedPositionReader>();
        reader.SetupGet(r => r.Name).Returns("reader/16");
        var services = new ServiceCollection()
            .AddScoped(_ => handler.Object)
            .AddScoped<IProjection>(_ => projection.Object)
            .AddScoped(_ => checkpoints.Object)
            .AddScoped(_ => reader.Object)
            .BuildServiceProvider();
        var rebuilder = new ProjectionRebuilder(grainFactory.Object, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CommitOrderOptions { PartitionCount = Partitions }), NoReplay(), Lease());

        await rebuilder.RebuildAsync("View");

        var steps = journal.Where((entry, index) => index == 0 || journal[index - 1] != entry).ToList();
        Assert.Equal(["pause", "reset", "truncate", "pause", "reset", "resume"], steps);
        Assert.Equal(2 * Partitions, journal.Count(entry => entry == "pause"));
    }

    /// <summary>
    /// A truncation that fails after a reader advanced — its pause lapsed while the projection was being emptied — still
    /// leaves every checkpoint at the beginning, so the projection re-reads the store when it resumes.
    /// </summary>
    [Fact]
    public async Task A_failed_truncation_still_leaves_the_checkpoints_at_the_beginning()
    {
        var journal = new List<string>();
        var (rebuilder, projection, checkpoints) = Build(journal);
        var positions = new Dictionary<int, long>();
        checkpoints
            .Setup(c => c.ResetAsync("View", It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, int partition, string _, CancellationToken _) => { lock (positions) { positions[partition] = 0; } })
            .Returns(Task.CompletedTask);
        projection
            .Setup(p => p.TruncateAsync(It.IsAny<CancellationToken>()))
            .Callback(() => { lock (positions) { positions[5] = 42; } })
            .ThrowsAsync(new InvalidOperationException("truncation failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilder.RebuildAsync("View"));

        Assert.Equal(Partitions, positions.Count);
        Assert.All(positions.Values, position => Assert.Equal(0, position));
        Assert.Equal(Partitions, journal.Count(entry => entry == "resume"));
    }

    [Fact]
    public async Task A_truncation_that_throws_leaves_the_checkpoints_at_the_beginning_and_resumes_every_partition()
    {
        var journal = new List<string>();
        var (rebuilder, projection, _) = Build(journal);
        projection.Setup(p => p.TruncateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("truncation failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilder.RebuildAsync("View"));

        Assert.Equal(2 * Partitions, journal.Count(entry => entry.StartsWith("reset", StringComparison.Ordinal)));
        Assert.Equal(Partitions, journal.Count(entry => entry == "resume"));
    }

    [Fact]
    public async Task A_pause_that_fails_resumes_what_it_paused_and_the_rebuild_reports_it()
    {
        var journal = new List<string>();
        var refusing = 0;
        var grains = Enumerable.Range(0, Partitions).ToDictionary(
            partition => StoreReaderGrainKey.Of("View", partition),
            partition => (IProjectionGrain)new FakeGrain(
                () =>
                {
                    if (partition == 3)
                    {
                        Interlocked.Increment(ref refusing);
                        throw new TimeoutException("the partition did not answer");
                    }

                    lock (journal)
                    {
                        journal.Add("pause");
                    }
                },
                () =>
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
        var reader = new Mock<ICommittedPositionReader>();
        reader.SetupGet(r => r.Name).Returns("reader/16");
        var services = new ServiceCollection()
            .AddScoped(_ => handler.Object)
            .AddScoped<IProjection>(_ => projection.Object)
            .AddScoped(_ => checkpoints.Object)
            .AddScoped(_ => reader.Object)
            .BuildServiceProvider();
        var rebuilder = new ProjectionRebuilder(grainFactory.Object, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CommitOrderOptions { PartitionCount = Partitions }), NoReplay(), Lease());

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilder.RebuildAsync("View"));

        Assert.Equal(1, refusing);
        Assert.Contains("could not be paused", failed.Message, StringComparison.Ordinal);
        Assert.Contains(StoreReaderGrainKey.Of("View", 3), failed.Message, StringComparison.Ordinal);
        Assert.Equal(journal.Count(entry => entry == "pause"), journal.Count(entry => entry == "resume"));
        projection.Verify(p => p.TruncateAsync(It.IsAny<CancellationToken>()), Times.Never);
        checkpoints.Verify(c => c.ResetAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The pause a reader counted before its answer was lost: the grain is paused, the caller's call fails, and the
    /// reader would stay paused for good unless the rebuild resumes every reader it reached.
    /// </summary>
    [Fact]
    public async Task A_pause_whose_answer_is_lost_is_resumed_all_the_same()
    {
        var journal = new List<string>();
        var grains = Enumerable.Range(0, Partitions).ToDictionary(
            partition => StoreReaderGrainKey.Of("View", partition),
            partition => (IProjectionGrain)new LosingGrain(partition == 3, journal));
        var grainFactory = new Mock<IGrainFactory>();
        grainFactory.Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null)).Returns((string key, string? _) => grains[key]);
        var projection = new Mock<IRebuildableProjection>();
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(projection.Object)).Returns("View");
        var reader = new Mock<ICommittedPositionReader>();
        reader.SetupGet(r => r.Name).Returns("reader/16");
        var services = new ServiceCollection()
            .AddScoped(_ => handler.Object)
            .AddScoped<IProjection>(_ => projection.Object)
            .AddScoped(_ => new Mock<IProjectionCheckpointStore>().Object)
            .AddScoped(_ => reader.Object)
            .BuildServiceProvider();
        var rebuilder = new ProjectionRebuilder(grainFactory.Object, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CommitOrderOptions { PartitionCount = Partitions }), NoReplay(), Lease());

        await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilder.RebuildAsync("View"));

        Assert.Equal(Partitions, journal.Count(entry => entry == "pause"));
        Assert.Equal(Partitions, journal.Count(entry => entry == "resume"));
    }

    /// <summary>
    /// Change let-a-projection-forget-a-deleted-tenant: the projection's record of deleted tenants is emptied just before its
    /// read model, between the two resets, and no other projection's record is touched.
    /// </summary>
    [Fact]
    public async Task A_rebuild_empties_the_projections_forgotten_tenants_with_its_truncation()
    {
        var journal = new List<string>();
        var forgotten = new Mock<IForgottenTenantStore>();
        forgotten.Setup(f => f.ClearAsync("View", It.IsAny<CancellationToken>()))
            .Callback(() => { lock (journal) { journal.Add("forget"); } })
            .Returns(Task.CompletedTask);
        var (rebuilder, projection, _) = Build(journal, forgotten.Object, forgets: true);
        projection.Setup(p => p.TruncateAsync(It.IsAny<CancellationToken>())).Callback(() => { lock (journal) { journal.Add("truncate"); } }).Returns(Task.CompletedTask);

        await rebuilder.RebuildAsync("View");

        Assert.Equal(Partitions, journal.IndexOf("forget"));
        Assert.Equal(Partitions + 1, journal.IndexOf("truncate"));
        forgotten.Verify(f => f.ClearAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Change let-a-projection-forget-a-deleted-tenant: a projection that does not declare it leaves the store alone.</summary>
    [Fact]
    public async Task A_rebuild_of_a_projection_that_does_not_forget_leaves_the_store_alone()
    {
        var journal = new List<string>();
        var forgotten = new Mock<IForgottenTenantStore>(MockBehavior.Strict);
        var (rebuilder, projection, _) = Build(journal, forgotten.Object);
        projection.Setup(p => p.TruncateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await rebuilder.RebuildAsync("View");

        projection.Verify(p => p.TruncateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (ProjectionRebuilder Rebuilder, Mock<IRebuildableProjection> Projection, Mock<IProjectionCheckpointStore> Checkpoints) Build(
        List<string> journal, IForgottenTenantStore? forgotten = null, bool forgets = false)
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
        if (forgets)
        {
            projection.As<IForgetsDeletedTenants>();
        }

        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(projection.Object)).Returns("View");
        var checkpoints = new Mock<IProjectionCheckpointStore>();
        checkpoints
            .Setup(c => c.ResetAsync("View", It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, int partition, string _, CancellationToken _) => { lock (journal) { journal.Add($"reset {partition}"); } })
            .Returns(Task.CompletedTask);
        var reader = new Mock<ICommittedPositionReader>();
        reader.SetupGet(r => r.Name).Returns("reader/16");

        var collection = new ServiceCollection()
            .AddScoped(_ => handler.Object)
            .AddScoped<IProjection>(_ => projection.Object)
            .AddScoped(_ => checkpoints.Object)
            .AddScoped(_ => reader.Object);
        if (forgotten is not null)
        {
            collection.AddScoped(_ => forgotten);
        }

        var services = collection.BuildServiceProvider();

        var rebuilder = new ProjectionRebuilder(grainFactory.Object, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CommitOrderOptions { PartitionCount = Partitions }), NoReplay(), Lease());
        return (rebuilder, projection, checkpoints);
    }

    /// <summary>Scenario <em>A rebuild is requested during a full replay</em>: refused, naming the replay, before any reader is paused.</summary>
    [Fact]
    public async Task A_rebuild_during_a_full_replay_is_refused_and_pauses_nothing()
    {
        var paused = 0;
        var grainFactory = new Mock<IGrainFactory>();
        grainFactory
            .Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null))
            .Returns((string _, string? _) => new FakeGrain(() => Interlocked.Increment(ref paused), () => Task.CompletedTask));
        var replay = new Mock<IProjectionReplayState>();
        replay.SetupGet(r => r.IsReplayActive).Returns(true);
        var rebuilder = new ProjectionRebuilder(
            grainFactory.Object,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CommitOrderOptions { PartitionCount = Partitions }),
            replay.Object,
            Lease());

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilder.RebuildAsync("View"));

        Assert.Contains("replay", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, paused);
    }

    private static StoreReaderLease Lease() => new(StoreReaderLease.DefaultDuration, TimeProvider.System);

    private static IProjectionReplayState NoReplay()
    {
        var replay = new Mock<IProjectionReplayState>();
        replay.SetupGet(r => r.IsReplayActive).Returns(false);
        return replay.Object;
    }

    /// <summary>A reader that counts its pauser before it answers, and whose answer is lost where it is told to lose it.</summary>
    private sealed class LosingGrain(bool losesTheAnswer, List<string> journal) : IProjectionGrain
    {
        public Task EnsureRunningAsync() => Task.CompletedTask;

        public Task NudgeAsync() => Task.CompletedTask;

        public Task<int> CatchUpAsync() => Task.FromResult(0);

        public Task<long> PositionAsync() => Task.FromResult(0L);

        public Task PauseAsync(Guid pauser, TimeSpan lease)
        {
            lock (journal)
            {
                journal.Add("pause");
            }

            return losesTheAnswer ? Task.FromException(new TimeoutException("the answer was lost")) : Task.CompletedTask;
        }

        public Task RenewPauseAsync(Guid pauser, TimeSpan lease) => Task.CompletedTask;

        public Task ResumeAsync(Guid pauser)
        {
            lock (journal)
            {
                journal.Add("resume");
            }

            return Task.CompletedTask;
        }

        public Task PauseAsync() => throw new InvalidOperationException("the parameterless pause is an older silo's");

        public Task ResumeAsync() => throw new InvalidOperationException("the parameterless resume is an older silo's");
    }

    /// <summary>The grain interface is internal, which a proxy generator cannot reach; a fake can.</summary>
    private sealed class FakeGrain(Action onPause, Func<Task> onResume) : IProjectionGrain
    {
        public Task EnsureRunningAsync() => Task.CompletedTask;

        public Task NudgeAsync() => Task.CompletedTask;

        public Task<int> CatchUpAsync() => Task.FromResult(0);

        public Task<long> PositionAsync() => Task.FromResult(0L);

        public Task PauseAsync(Guid pauser, TimeSpan lease)
        {
            onPause();
            return Task.CompletedTask;
        }

        public Task RenewPauseAsync(Guid pauser, TimeSpan lease) => Task.CompletedTask;

        public Task ResumeAsync(Guid pauser) => onResume();

        public Task PauseAsync() => throw new InvalidOperationException("the parameterless pause is an older silo's");

        public Task ResumeAsync() => throw new InvalidOperationException("the parameterless resume is an older silo's");
    }
}
