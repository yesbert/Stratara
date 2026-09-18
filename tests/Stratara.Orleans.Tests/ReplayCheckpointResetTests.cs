using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A full replay on a host with store-reading projections pauses their readers, returns their checkpoints
/// to the beginning before the read models are emptied, and resumes them however the truncation ends; a
/// truncator registered after the store-reading projections is refused at start.
/// </summary>
public sealed class ReplayCheckpointResetTests
{
    private const int Partitions = 2;

    [Fact]
    public async Task Readers_are_paused_and_their_checkpoints_reset_before_the_truncation_and_resumed_after()
    {
        var journal = new List<string>();
        var truncator = new Mock<IProjectionViewTruncator>();
        truncator.Setup(t => t.TruncateAllAsync(It.IsAny<CancellationToken>())).Callback(() => journal.Add("truncate")).Returns(Task.CompletedTask);

        var decorated = Build(journal, truncator.Object);
        await decorated.TruncateAllAsync();

        Assert.Equal(["pause", "pause", "reset View/0", "reset View/1", "truncate", "resume", "resume"], Normalised(journal));
    }

    [Fact]
    public async Task Readers_are_resumed_when_the_truncation_fails()
    {
        var journal = new List<string>();
        var truncator = new Mock<IProjectionViewTruncator>();
        truncator.Setup(t => t.TruncateAllAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("truncation failed"));

        var decorated = Build(journal, truncator.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => decorated.TruncateAllAsync());

        Assert.Equal(2, journal.Count(entry => entry == "resume"));
        Assert.Equal(2, journal.Count(entry => entry.StartsWith("reset", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_truncator_registered_before_the_store_readers_is_wrapped_and_the_host_starts()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new Mock<IGrainFactory>().Object)
            .AddScoped(_ => new Mock<IProjectionViewTruncator>().Object)
            .AddStrataraProjectionGrains();

        await using var provider = services.BuildServiceProvider();
        var check = provider.GetServices<IHostedService>().OfType<ReplayTruncatorOrderCheck>().Single();

        await check.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_truncator_registered_after_the_store_readers_is_refused_at_start()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddStrataraProjectionGrains()
            .AddScoped(_ => new Mock<IProjectionViewTruncator>().Object);

        await using var provider = services.BuildServiceProvider();
        var check = provider.GetServices<IHostedService>().OfType<ReplayTruncatorOrderCheck>().Single();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));
        Assert.Contains("before AddStrataraProjectionGrains", refused.Message, StringComparison.Ordinal);
    }

    private static ReplayCheckpointResetTruncator Build(List<string> journal, IProjectionViewTruncator inner)
    {
        var grains = Enumerable.Range(0, Partitions).ToDictionary(
            partition => StoreReaderGrainKey.Of("View", partition),
            _ => (IProjectionGrain)new JournalGrain(journal));
        var grainFactory = new Mock<IGrainFactory>();
        grainFactory.Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null)).Returns((string key, string? _) => grains[key]);

        var projection = new Mock<IProjection>();
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(projection.Object)).Returns("View");
        var checkpoints = new Mock<IProjectionCheckpointStore>();
        checkpoints
            .Setup(c => c.ResetAsync("View", It.IsAny<int>(), "reader/2", It.IsAny<CancellationToken>()))
            .Callback((string name, int partition, string _, CancellationToken _) => { lock (journal) { journal.Add($"reset {name}/{partition}"); } })
            .Returns(Task.CompletedTask);
        var reader = new Mock<ICommittedPositionReader>();
        reader.SetupGet(r => r.Name).Returns("reader/2");

        var services = new ServiceCollection()
            .AddScoped(_ => handler.Object)
            .AddScoped(_ => projection.Object)
            .AddScoped(_ => checkpoints.Object)
            .AddScoped(_ => reader.Object)
            .BuildServiceProvider();

        return new ReplayCheckpointResetTruncator(inner, grainFactory.Object, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CommitOrderOptions { PartitionCount = Partitions }));
    }

    /// <summary>Parallel pauses, resets and resumes land in any order within their step; the steps' order is what counts.</summary>
    private static List<string> Normalised(List<string> journal) =>
    [
        .. journal.Where(e => e == "pause"),
        .. journal.Where(e => e.StartsWith("reset", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
        .. journal.Where(e => e == "truncate"),
        .. journal.Where(e => e == "resume"),
    ];

    private sealed class JournalGrain(List<string> journal) : IProjectionGrain
    {
        public Task EnsureRunningAsync() => Task.CompletedTask;

        public Task NudgeAsync() => Task.CompletedTask;

        public Task<int> CatchUpAsync() => Task.FromResult(0);

        public Task<long> PositionAsync() => Task.FromResult(0L);

        public Task PauseAsync()
        {
            lock (journal)
            {
                journal.Add("pause");
            }

            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            lock (journal)
            {
                journal.Add("resume");
            }

            return Task.CompletedTask;
        }
    }
}
