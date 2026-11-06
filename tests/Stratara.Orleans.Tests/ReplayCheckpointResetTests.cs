using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Diagnostics;
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

        Assert.Equal(["pause", "pause", "reset View/0", "reset View/1", "truncate", "pause", "pause", "reset View/0", "reset View/1", "resume", "resume"], Normalised(journal));
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
        Assert.Equal(4, journal.Count(entry => entry.StartsWith("reset", StringComparison.Ordinal)));
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

    [Fact]
    public async Task A_truncator_that_empties_the_table_leaves_the_saga_positions_intact_and_logs()
    {
        var store = new FakeStore(Saga("A", 0, 12), Saga("A", 1, 30), Saga("B", 0, 7), new StoredCheckpoint("View", 0, Reader, 99));
        var logger = new RecordingLogger();

        await Build([], new EmptyingTruncator(store), store, logger).TruncateAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, store.PositionOf("sagas:A", 0));
        Assert.Equal(30, store.PositionOf("sagas:A", 1));
        Assert.Equal(7, store.PositionOf("sagas:B", 0));
        Assert.Equal(0, store.PositionOf("View", 0));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(LogEvents.Orleans.ReplaySagaCheckpointsRestored, warning.EventId);
        Assert.Contains(nameof(EmptyingTruncator), warning.Message, StringComparison.Ordinal);
        Assert.Contains("sagas:A/1=30", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_positions_are_remembered_before_the_first_reset()
    {
        var journal = new List<string>();
        var store = new FakeStore(journal, Saga("A", 0, 12));

        await Build(journal, new EmptyingTruncator(store), store).TruncateAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal("list", journal.First(e => e == "list" || e.StartsWith("reset", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_truncator_that_touches_nothing_causes_no_write_and_no_warning()
    {
        var store = new FakeStore(Saga("A", 0, 12));
        var logger = new RecordingLogger();

        await Build([], Mock.Of<IProjectionViewTruncator>(), store, logger).TruncateAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.SagaWrites);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task A_position_moved_back_is_raised_and_one_further_on_is_left()
    {
        var store = new FakeStore(Saga("Behind", 0, 12), Saga("Ahead", 0, 20), Saga("Same", 0, 5));
        var truncator = new CallbackTruncator(() =>
        {
            store.Put(Saga("Behind", 0, 0));
            store.Put(Saga("Ahead", 0, 25));
        });

        await Build([], truncator, store).TruncateAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, store.PositionOf("sagas:Behind", 0));
        Assert.Equal(25, store.PositionOf("sagas:Ahead", 0));
        Assert.Equal(["sagas:Behind/0"], store.SagaWrites);
    }

    [Fact]
    public async Task A_row_under_another_reader_is_left_because_positions_are_not_comparable()
    {
        var store = new FakeStore(Saga("A", 0, 12));
        var truncator = new CallbackTruncator(() => store.Put(new StoredCheckpoint("sagas:A", 0, "other/2", 1)));

        await Build([], truncator, store).TruncateAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new StoredCheckpoint("sagas:A", 0, "other/2", 1), store.Row("sagas:A", 0));
        Assert.Empty(store.SagaWrites);
    }

    [Fact]
    public async Task The_shared_row_of_older_releases_is_kept_and_a_projection_with_a_similar_name_is_not()
    {
        var store = new FakeStore(new StoredCheckpoint("sagas", 0, Reader, 40), new StoredCheckpoint("sagasView", 0, Reader, 8));

        await Build([], new EmptyingTruncator(store), store).TruncateAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(40, store.PositionOf("sagas", 0));
        Assert.Null(store.Row("sagasView", 0));
    }

    [Fact]
    public async Task A_truncator_that_fails_part_way_has_the_positions_written_back_and_its_failure_stands()
    {
        var store = new FakeStore(Saga("A", 0, 12));
        var truncator = new CallbackTruncator(() =>
        {
            store.Clear();
            throw new InvalidOperationException("truncation failed");
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Build([], truncator, store).TruncateAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal("truncation failed", failure.Message);
        Assert.Equal(12, store.PositionOf("sagas:A", 0));
    }

    [Fact]
    public async Task A_cancellation_during_the_truncation_still_writes_the_positions_back()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeStore(Saga("A", 0, 12));
        var truncator = new CallbackTruncator(() =>
        {
            store.Clear();
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build([], truncator, store).TruncateAllAsync(cancellation.Token));

        Assert.Equal(12, store.PositionOf("sagas:A", 0));
    }

    [Fact]
    public async Task Positions_that_cannot_be_remembered_fail_before_the_truncator_or_a_reset_and_the_readers_are_resumed()
    {
        var journal = new List<string>();
        var store = new FakeStore(journal, Saga("A", 0, 12)) { ListFailure = new InvalidOperationException("the store is unreachable") };
        var truncator = new Mock<IProjectionViewTruncator>(MockBehavior.Strict);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(journal, truncator.Object, store).TruncateAllAsync(TestContext.Current.CancellationToken));

        truncator.VerifyNoOtherCalls();
        Assert.DoesNotContain(journal, e => e.StartsWith("reset", StringComparison.Ordinal));
        Assert.Equal(2, journal.Count(e => e == "resume"));
    }

    [Fact]
    public async Task A_position_that_cannot_be_written_back_fails_the_replay_and_names_it_while_the_others_are_written()
    {
        var store = new FakeStore(Saga("A", 0, 12), Saga("B", 0, 7)) { RefusedWrite = "sagas:A" };
        var logger = new RecordingLogger();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build([], new EmptyingTruncator(store), store, logger).TruncateAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(7, store.PositionOf("sagas:B", 0));
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(LogEvents.Orleans.ReplaySagaCheckpointsNotRestored, error.EventId);
        Assert.Contains("sagas:B/0=7", Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning).Message, StringComparison.Ordinal);
        Assert.Contains("sagas:A/0=12", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sagas:B", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncation_and_a_write_back_that_both_fail_report_both()
    {
        var store = new FakeStore(Saga("A", 0, 12)) { RefusedWrite = "sagas:A" };
        var truncator = new CallbackTruncator(() =>
        {
            store.Clear();
            throw new TimeoutException("truncation failed");
        });

        var failure = await Assert.ThrowsAsync<AggregateException>(() => Build([], truncator, store).TruncateAllAsync(TestContext.Current.CancellationToken));

        Assert.Contains(failure.InnerExceptions, e => e is TimeoutException);
        Assert.Contains(failure.InnerExceptions, e => e is InvalidOperationException);
    }

    [Fact]
    public async Task A_store_that_cannot_list_replays_as_before_and_warns()
    {
        var truncated = false;
        var logger = new RecordingLogger();

        await Build([], new CallbackTruncator(() => truncated = true), new UnlistingStore(), logger).TruncateAllAsync(TestContext.Current.CancellationToken);

        Assert.True(truncated);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(LogEvents.Orleans.ReplaySagaCheckpointsNotGuarded, warning.EventId);
        Assert.Contains(nameof(UnlistingStore), warning.Message, StringComparison.Ordinal);
    }

    private const string Reader = "reader/2";

    private static StoredCheckpoint Saga(string name, int partition, long position) => new($"sagas:{name}", partition, Reader, position);

    private static ReplayCheckpointResetTruncator Build(List<string> journal, IProjectionViewTruncator inner, IProjectionCheckpointStore store, ILogger<ReplayCheckpointResetTruncator>? logger = null) =>
        Build(journal, inner, Mock.Of<ICommittedPositionReader>(r => r.Name == Reader), store, logger);

    private static ReplayCheckpointResetTruncator Build(List<string> journal, IProjectionViewTruncator inner)
    {
        var checkpoints = new Mock<IProjectionCheckpointStore>();
        checkpoints.Setup(c => c.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        checkpoints
            .Setup(c => c.ResetAsync("View", It.IsAny<int>(), "reader/2", It.IsAny<CancellationToken>()))
            .Callback((string name, int partition, string _, CancellationToken _) => { lock (journal) { journal.Add($"reset {name}/{partition}"); } })
            .Returns(Task.CompletedTask);
        var reader = new Mock<ICommittedPositionReader>();
        reader.SetupGet(r => r.Name).Returns("reader/2");
        return Build(journal, inner, reader.Object, checkpoints.Object, null);
    }

    private static ReplayCheckpointResetTruncator Build(List<string> journal, IProjectionViewTruncator inner, ICommittedPositionReader reader, IProjectionCheckpointStore checkpoints, ILogger<ReplayCheckpointResetTruncator>? logger)
    {
        var grains = Enumerable.Range(0, Partitions).ToDictionary(
            partition => StoreReaderGrainKey.Of("View", partition),
            _ => (IProjectionGrain)new JournalGrain(journal));
        var grainFactory = new Mock<IGrainFactory>();
        grainFactory.Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null)).Returns((string key, string? _) => grains[key]);

        var projection = new Mock<IProjection>();
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(projection.Object)).Returns("View");
        var services = new ServiceCollection()
            .AddScoped(_ => handler.Object)
            .AddScoped(_ => projection.Object)
            .AddScoped(_ => checkpoints)
            .AddScoped(_ => reader)
            .BuildServiceProvider();

        return new ReplayCheckpointResetTruncator(inner, grainFactory.Object, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CommitOrderOptions { PartitionCount = Partitions }), new ProjectionRegistrations(services.GetRequiredService<IServiceScopeFactory>()), new StoreReaderLease(StoreReaderLease.DefaultDuration, TimeProvider.System), logger);
    }

    /// <summary>
    /// Parallel pauses, resets and resumes land in any order within their step; the steps' order is what counts. The
    /// resets are sorted on either side of the truncation.
    /// </summary>
    private static List<string> Normalised(List<string> journal)
    {
        var truncation = journal.IndexOf("truncate");
        return
        [
            .. journal.Take(truncation).Where(e => e == "pause"),
            .. journal.Take(truncation).Where(e => e.StartsWith("reset", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
            .. journal.Where(e => e == "truncate"),
            .. journal.Skip(truncation + 1).Where(e => e == "pause"),
            .. journal.Skip(truncation + 1).Where(e => e.StartsWith("reset", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
            .. journal.Where(e => e == "resume"),
        ];
    }

    private sealed class RecordingLogger : ILogger<ReplayCheckpointResetTruncator>
    {
        public List<(LogLevel Level, int EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
    }

    private sealed class EmptyingTruncator(FakeStore store) : IProjectionViewTruncator
    {
        public Task TruncateAllAsync(CancellationToken cancellationToken = default)
        {
            store.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class CallbackTruncator(Action truncate) : IProjectionViewTruncator
    {
        public Task TruncateAllAsync(CancellationToken cancellationToken = default)
        {
            truncate();
            return Task.CompletedTask;
        }
    }

    private sealed class UnlistingStore : IProjectionCheckpointStore
    {
        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A store honouring the token, as the framework's does, and refusing what a test names.</summary>
    private sealed class FakeStore(List<string> journal, params StoredCheckpoint[] rows) : IProjectionCheckpointStore
    {
        private readonly Dictionary<(string, int), StoredCheckpoint> _rows = rows.ToDictionary(r => (r.Consumer, r.Partition));

        public FakeStore(params StoredCheckpoint[] rows) : this([], rows)
        {
        }

        public List<string> SagaWrites { get; } = [];

        public Exception? ListFailure { get; init; }

        public string? RefusedWrite { get; init; }

        public void Clear()
        {
            lock (_rows)
            {
                _rows.Clear();
            }
        }

        public void Put(StoredCheckpoint row)
        {
            lock (_rows)
            {
                _rows[(row.Consumer, row.Partition)] = row;
            }
        }

        public StoredCheckpoint? Row(string consumer, int partition)
        {
            lock (_rows)
            {
                return _rows.GetValueOrDefault((consumer, partition));
            }
        }

        public long? PositionOf(string consumer, int partition) => Row(consumer, partition)?.Position;

        public Task<IReadOnlyList<StoredCheckpoint>> ListAsync(string consumerPrefix, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ListFailure is not null)
            {
                throw ListFailure;
            }

            lock (journal)
            {
                journal.Add("list");
            }

            lock (_rows)
            {
                return Task.FromResult<IReadOnlyList<StoredCheckpoint>>(_rows.Values.Where(r => r.Consumer.StartsWith(consumerPrefix, StringComparison.Ordinal)).ToList());
            }
        }

        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default) =>
            Task.FromResult(PositionOf(projection, partition) ?? 0);

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Refuse(projection);
            lock (_rows)
            {
                SagaWrites.Add($"{projection}/{partition}");
                _rows[(projection, partition)] = new StoredCheckpoint(projection, partition, reader, position);
            }

            return Task.CompletedTask;
        }

        public Task<bool> CreateAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Refuse(projection);
            lock (_rows)
            {
                SagaWrites.Add($"{projection}/{partition}");
                return Task.FromResult(_rows.TryAdd((projection, partition), new StoredCheckpoint(projection, partition, reader, position)));
            }
        }

        public Task ResetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default)
        {
            lock (journal)
            {
                journal.Add($"reset {projection}/{partition}");
            }

            lock (_rows)
            {
                _rows[(projection, partition)] = new StoredCheckpoint(projection, partition, reader, 0);
            }

            return Task.CompletedTask;
        }

        private void Refuse(string consumer)
        {
            if (consumer == RefusedWrite)
            {
                throw new InvalidOperationException($"{consumer} cannot be written");
            }
        }
    }

    private sealed class JournalGrain(List<string> journal) : IProjectionGrain
    {
        public Task EnsureRunningAsync() => Task.CompletedTask;

        public Task NudgeAsync() => Task.CompletedTask;

        public Task<int> CatchUpAsync() => Task.FromResult(0);

        public Task<long> PositionAsync() => Task.FromResult(0L);


        public Task<StoreReaderProgress> WaitForAsync(long position, TimeSpan budget) => Task.FromResult(new StoreReaderProgress(false, StoreReaderState.Advancing));

        public Task PauseAsync(Guid pauser, TimeSpan lease)
        {
            lock (journal)
            {
                journal.Add("pause");
            }

            return Task.CompletedTask;
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
}
