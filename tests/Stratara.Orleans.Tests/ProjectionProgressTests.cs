using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// How the wait for a projection asks its partitions' grains and reads their answers: a mark from the committed head of
/// every partition, a partition without entries counted as reached, a partition answered short asked again, the states
/// at the limit reduced to one outcome — stalled before suspended before timed out — a mark of another reader or a
/// retired grain answered as not applicable, and each call's budget capped.
/// </summary>
public sealed class ProjectionProgressTests
{
    private const string Projection = "Orders";
    private const string Reader = "fake/4";

    [Fact]
    public async Task A_mark_holds_the_committed_head_of_every_partition_and_the_readers_name()
    {
        var progress = new Setup().Build();

        var mark = await progress.Progress.MarkAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Reader, mark.Reader);
        Assert.Equal([10L, 0L, 30L, 40L], mark.Positions);
    }

    [Fact]
    public async Task Partitions_without_entries_are_not_asked_and_the_others_reach_the_mark()
    {
        var setup = new Setup();
        var progress = setup.Build();

        var result = await progress.Progress.WaitAsync(Projection, Mark(10, 0, 30, 0), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.IsReached);
        Assert.Empty(result.Behind);
        Assert.Equal([0, 2], setup.Calls.Select(call => call.Partition).Order());
    }

    [Fact]
    public async Task A_partition_answered_short_is_asked_again()
    {
        var setup = new Setup();
        setup.Answer(1, Short(StoreReaderState.Advancing), Reached());
        var progress = setup.Build();

        var result = await progress.Progress.WaitAsync(Projection, Mark(0, 5, 0, 0), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.IsReached);
        Assert.Equal(2, setup.Calls.Count(call => call.Partition == 1));
    }

    [Fact]
    public async Task A_partition_that_reached_the_mark_is_asked_again_until_every_partition_reaches_it_in_one_round()
    {
        var setup = new Setup();
        setup.Answer(0, Reached(), Short(StoreReaderState.Suspended), Reached());
        setup.Answer(1, Short(StoreReaderState.Advancing), Reached(), Reached());
        var progress = setup.Build();

        var result = await progress.Progress.WaitAsync(Projection, Mark(5, 5, 0, 0), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.IsReached);
        Assert.Equal(3, setup.Calls.Count(call => call.Partition == 0));
        Assert.Equal(3, setup.Calls.Count(call => call.Partition == 1));
    }

    [Fact]
    public void Two_marks_with_the_same_reader_and_positions_are_equal()
    {
        Assert.Equal(Mark(1, 2, 3, 4), Mark(1, 2, 3, 4));
        Assert.Equal(Mark(1, 2, 3, 4).GetHashCode(), Mark(1, 2, 3, 4).GetHashCode());
        Assert.NotEqual(Mark(1, 2, 3, 4), Mark(1, 2, 3, 5));
    }

    [Theory]
    [InlineData((int)StoreReaderState.StalledOnEntry, (int)StoreReaderState.Suspended, ProjectionWaitOutcome.Stalled)]
    [InlineData((int)StoreReaderState.StalledOnRead, (int)StoreReaderState.Advancing, ProjectionWaitOutcome.Stalled)]
    [InlineData((int)StoreReaderState.Suspended, (int)StoreReaderState.Advancing, ProjectionWaitOutcome.Suspended)]
    [InlineData((int)StoreReaderState.Advancing, (int)StoreReaderState.Advancing, ProjectionWaitOutcome.TimedOut)]
    [InlineData((int)StoreReaderState.Unregistered, (int)StoreReaderState.Advancing, ProjectionWaitOutcome.TimedOut)]
    public async Task At_the_limit_the_most_telling_state_is_the_outcome_and_every_partition_behind_is_named(
        int first, int second, ProjectionWaitOutcome expected)
    {
        var setup = new Setup();
        setup.Answer(0, Timed((StoreReaderState)first));
        setup.Answer(3, Timed((StoreReaderState)second));
        var progress = setup.Build();

        var result = await progress.Progress.WaitAsync(Projection, Mark(1, 0, 1, 1), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.False(result.IsReached);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal([0, 3], result.Behind.Select(lag => lag.Partition));
    }

    [Fact]
    public async Task A_mark_of_another_reader_is_not_applicable_and_no_grain_is_asked()
    {
        var setup = new Setup();
        var progress = setup.Build();

        var result = await progress.Progress.WaitAsync(Projection, new StoreMark("fake/8", [1, 1, 1, 1, 1, 1, 1, 1]), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ProjectionWaitOutcome.MarkNotApplicable, result.Outcome);
        Assert.Empty(setup.Calls);
    }

    [Fact]
    public async Task A_retired_reader_makes_the_mark_not_applicable()
    {
        var setup = new Setup();
        setup.Answer(2, Short(StoreReaderState.Retired));
        var progress = setup.Build();

        var result = await progress.Progress.WaitAsync(Projection, Mark(0, 0, 1, 0), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ProjectionWaitOutcome.MarkNotApplicable, result.Outcome);
    }

    [Fact]
    public async Task A_reader_answering_from_a_silo_that_does_not_register_it_is_asked_again()
    {
        var setup = new Setup();
        setup.Answer(2, Short(StoreReaderState.Unregistered), Reached());
        var progress = setup.Build();

        var result = await progress.Progress.WaitAsync(Projection, Mark(0, 0, 1, 0), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ProjectionWaitOutcome.Reached, result.Outcome);
    }

    [Fact]
    public async Task A_call_never_waits_longer_than_the_cap_and_a_zero_timeout_waits_not_at_all()
    {
        var setup = new Setup();
        setup.Answer(0, Timed(StoreReaderState.Advancing), Reached());
        setup.Answer(1, Timed(StoreReaderState.Advancing));
        var progress = setup.Build();

        await progress.Progress.WaitAsync(Projection, Mark(1, 0, 0, 0), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var immediate = await progress.Progress.WaitAsync(Projection, Mark(0, 1, 0, 0), TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.All(setup.Calls.Where(call => call.Partition == 0), call => Assert.Equal(ProjectionProgress.MaximumBudget, call.Budget));
        Assert.Equal(TimeSpan.Zero, Assert.Single(setup.Calls, call => call.Partition == 1).Budget);
        Assert.Equal(ProjectionWaitOutcome.TimedOut, immediate.Outcome);
    }

    [Fact]
    public async Task A_projection_is_found_by_its_type()
    {
        var setup = new Setup();
        var progress = setup.Build();

        var result = await progress.Progress.WaitAsync(progress.ProjectionType, Mark(1, 0, 0, 0), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.IsReached);
        Assert.Equal(StoreReaderGrainKey.Of(Projection, 0), Assert.Single(setup.Calls).Key);
    }

    [Fact]
    public async Task An_unknown_projection_and_a_negative_timeout_are_refused()
    {
        var progress = new Setup().Build().Progress;

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => progress.WaitAsync("Invoices", Mark(1, 0, 0, 0), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => progress.WaitAsync(typeof(string), Mark(1, 0, 0, 0), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => progress.WaitAsync(Projection, Mark(1, 0, 0, 0), TimeSpan.FromSeconds(-1), TestContext.Current.CancellationToken));

        Assert.Contains("Invoices", unknown.Message, StringComparison.Ordinal);
    }

    private static StoreMark Mark(params long[] positions) => new(Reader, positions);

    private static Func<FakeTimeProvider, TimeSpan, StoreReaderProgress> Reached() => (_, _) => new StoreReaderProgress(true, StoreReaderState.Advancing);

    private static Func<FakeTimeProvider, TimeSpan, StoreReaderProgress> Short(StoreReaderState state) => (_, _) => new StoreReaderProgress(false, state);

    /// <summary>Spends the whole budget on the clock, as a grain that waited it out does, and answers short.</summary>
    private static Func<FakeTimeProvider, TimeSpan, StoreReaderProgress> Timed(StoreReaderState state) => (clock, budget) =>
    {
        clock.Advance(budget);
        return new StoreReaderProgress(false, state);
    };

    private sealed record Call(string Key, int Partition, long Position, TimeSpan Budget);

    private sealed class Setup
    {
        private readonly Dictionary<int, Queue<Func<FakeTimeProvider, TimeSpan, StoreReaderProgress>>> _answers = [];

        public List<Call> Calls { get; } = [];

        public void Answer(int partition, params Func<FakeTimeProvider, TimeSpan, StoreReaderProgress>[] answers) =>
            _answers[partition] = new Queue<Func<FakeTimeProvider, TimeSpan, StoreReaderProgress>>(answers);

        public (ProjectionProgress Progress, Type ProjectionType) Build()
        {
            var clock = new FakeTimeProvider();
            var projection = new Mock<IProjection>().Object;
            var handler = new Mock<IProjectionHandler>();
            handler.Setup(h => h.GetProjectionName(projection)).Returns(Projection);
            var reader = new Mock<ICommittedPositionReader>();
            reader.SetupGet(r => r.Name).Returns(Reader);
            reader.Setup(r => r.CommittedHeadAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int partition, CancellationToken _) => partition == 1 ? 0 : (partition + 1) * 10L);

            var services = new ServiceCollection()
                .AddSingleton(handler.Object)
                .AddSingleton(projection)
                .AddSingleton(reader.Object)
                .BuildServiceProvider();
            var grains = new Mock<IGrainFactory>();
            grains.Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null))
                .Returns((string key, string? _) => new ScriptedGrain(key, this, clock));

            var progress = new ProjectionProgress(
                grains.Object,
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new CommitOrderOptions { PartitionCount = 4 }),
                new ProjectionRegistrations(services.GetRequiredService<IServiceScopeFactory>()),
                clock);
            return (progress, projection.GetType());
        }

        public StoreReaderProgress Next(string key, long position, TimeSpan budget, FakeTimeProvider clock)
        {
            var partition = StoreReaderGrainKey.Parse(key).Partition;
            lock (Calls)
            {
                Calls.Add(new Call(key, partition, position, budget));
                return _answers.TryGetValue(partition, out var answers) && answers.Count > 0
                    ? answers.Dequeue()(clock, budget)
                    : new StoreReaderProgress(true, StoreReaderState.Advancing);
            }
        }
    }

    /// <summary>A projection grain that answers waits from the test's script; the internal interface cannot be proxied.</summary>
    private sealed class ScriptedGrain(string key, Setup setup, FakeTimeProvider clock) : IProjectionGrain
    {
        public Task<StoreReaderProgress> WaitForAsync(long position, TimeSpan budget) => Task.FromResult(setup.Next(key, position, budget, clock));

        public Task EnsureRunningAsync() => Task.CompletedTask;

        public Task NudgeAsync() => Task.CompletedTask;

        public Task<int> CatchUpAsync() => Task.FromResult(0);

        public Task<long> PositionAsync() => Task.FromResult(0L);

        public Task PauseAsync(Guid pauser, TimeSpan lease) => Task.CompletedTask;

        public Task PauseAsync() => Task.CompletedTask;

        public Task ResumeAsync(Guid pauser) => Task.CompletedTask;

        public Task ResumeAsync() => Task.CompletedTask;

        public Task RenewPauseAsync(Guid pauser, TimeSpan lease) => Task.CompletedTask;
    }
}
