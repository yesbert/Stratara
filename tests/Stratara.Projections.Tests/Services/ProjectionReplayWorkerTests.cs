using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Stratara.Diagnostics;
using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Shared.EventSourcing;

namespace Stratara.Projections.Tests.Services;

public class ProjectionReplayWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_RegistersReplaySubscription_DoesNotRunReplayWithoutTrigger()
    {
        var harness = new Harness();
        await harness.RunAsync(triggerReplay: false);

        harness.ReplayState.Verify(
            s => s.SubscribeToReplayRequestAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.ReplayState.Verify(s => s.Activate(), Times.Never);
        harness.ViewTruncator.Verify(t => t.TruncateAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReplayCallback_HappyPath_ActivatesTruncatesReplaysAndDeactivates()
    {
        var harness = new Harness();
        var entry = NewEntry(sequenceNumber: 1);
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        SetupBatchSequence(harness, [[entry], []]);

        await harness.RunAsync(triggerReplay: true);

        harness.ReplayState.Verify(s => s.Activate(), Times.Once);
        harness.ViewTruncator.Verify(t => t.TruncateAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.ProjectionManager.Verify(
            m => m.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.ReplayState.Verify(s => s.Deactivate(), Times.Once);
        harness.ReplayState.Verify(s => s.SetProgress(1, 1), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReplayCallback_EmptyStream_StillTruncatesAndDeactivates()
    {
        var harness = new Harness();
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        SetupBatchSequence(harness, [[]]);

        await harness.RunAsync(triggerReplay: true);

        harness.ViewTruncator.Verify(t => t.TruncateAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.ProjectionManager.Verify(
            m => m.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.ReplayState.Verify(s => s.Deactivate(), Times.Once);
    }

    [Fact]
    public async Task ReplayCallback_MultipleBatches_IncrementsAfterSequenceBetweenBatches()
    {
        var harness = new Harness();
        var entry1 = NewEntry(sequenceNumber: 10);
        var entry2 = NewEntry(sequenceNumber: 20);
        var entry3 = NewEntry(sequenceNumber: 30);
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(30);

        var capturedAfterSequences = new List<long>();
        harness.EventStreamRepository
            .Setup(r => r.GetManyAfterSequenceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<long, int, CancellationToken>((afterSeq, _, _) =>
            {
                capturedAfterSequences.Add(afterSeq);
                return Task.FromResult<IReadOnlyList<EventStreamEntry>>(afterSeq switch
                {
                    0 => [entry1, entry2],
                    20 => [entry3],
                    _ => [],
                });
            });

        await harness.RunAsync(triggerReplay: true);

        Assert.Equal([0L, 20L, 30L], capturedAfterSequences);
        harness.ProjectionManager.Verify(
            m => m.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task ReplayCallback_FailureInReplay_CallsSetFailedWithExceptionMessage()
    {
        var harness = new Harness();
        harness.ViewTruncator
            .Setup(t => t.TruncateAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("truncate failed"));

        await harness.RunAsync(triggerReplay: true);

        harness.ReplayState.Verify(s => s.SetFailed(It.Is<string>(m => m.Contains("truncate failed"))), Times.Once);
        harness.ReplayState.Verify(s => s.Deactivate(), Times.Once);
    }

    [Fact]
    public async Task ReplayCallback_FailureMessageOver500Chars_IsTruncatedWithEllipsis()
    {
        var harness = new Harness();
        var longMessage = new string('x', 600);
        harness.ViewTruncator
            .Setup(t => t.TruncateAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(longMessage));

        await harness.RunAsync(triggerReplay: true);

        harness.ReplayState.Verify(
            s => s.SetFailed(It.Is<string>(m => m.Length == 501 && m.EndsWith('…'))),
            Times.Once);
    }

    [Fact]
    public async Task ReplayCallback_OperationCanceledException_IsSwallowedNoSetFailed()
    {
        var harness = new Harness();
        harness.ViewTruncator
            .Setup(t => t.TruncateAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await harness.RunAsync(triggerReplay: true);

        harness.ReplayState.Verify(s => s.SetFailed(It.IsAny<string>()), Times.Never);
        harness.ReplayState.Verify(s => s.Deactivate(), Times.Once);
    }

    [Fact]
    public async Task ReplayCallback_RestoresSessionContextPerEntry()
    {
        var harness = new Harness();
        var tenantId = Guid.NewGuid();
        var actorTenantId = Guid.NewGuid();
        var entry = NewEntry(sequenceNumber: 1, tenantId: tenantId, actorTenantId: actorTenantId);
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        SetupBatchSequence(harness, [[entry], []]);

        await harness.RunAsync(triggerReplay: true);

        harness.SessionContextProvider.Verify(
            s => s.Set(It.Is<Contracts.Session.SessionContext>(c =>
                c.TenantId == tenantId && c.ActorTenantId == actorTenantId)),
            Times.Once);
    }

    [Fact]
    public async Task ReplayCallback_BatchFailsOnceThenSucceeds_ReplayCompletesAndBatchIsReapplied()
    {
        var harness = new Harness(batchPipeline: FastRetryPipeline());
        var entry = NewEntry(sequenceNumber: 1);
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        harness.EventStreamRepository
            .Setup(r => r.GetManyAfterSequenceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<long, int, CancellationToken>((afterSeq, _, _) =>
                Task.FromResult<IReadOnlyList<EventStreamEntry>>(afterSeq == 0 ? [entry] : []));
        var calls = 0;
        harness.ProjectionManager
            .Setup(m => m.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1
                ? Task.FromException(new TimeoutException("read store busy"))
                : Task.CompletedTask);

        await harness.RunAsync(triggerReplay: true);

        Assert.Equal(2, calls);
        harness.ReplayState.Verify(s => s.SetFailed(It.IsAny<string>()), Times.Never);
        harness.ReplayState.Verify(s => s.SetProgress(1, 1), Times.AtLeastOnce);
        harness.ReplayState.Verify(s => s.Deactivate(), Times.Once);
        var retry = Assert.Single(harness.Logger.Entries, e => e.EventId == LogEvents.Projection.ProjectionReplayBatchFailed);
        Assert.Equal(LogLevel.Warning, retry.Level);
        Assert.Contains("attempt 1", retry.Message);
    }

    [Fact]
    public async Task ReplayCallback_BatchReadFailsOnceThenSucceeds_ReplayContinuesFromTheSameSequence()
    {
        var harness = new Harness(batchPipeline: FastRetryPipeline());
        var entry = NewEntry(sequenceNumber: 1);
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var capturedAfterSequences = new List<long>();
        harness.EventStreamRepository
            .Setup(r => r.GetManyAfterSequenceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<long, int, CancellationToken>((afterSeq, _, _) =>
            {
                capturedAfterSequences.Add(afterSeq);
                if (capturedAfterSequences.Count == 1)
                {
                    throw new TimeoutException("event store busy");
                }

                return Task.FromResult<IReadOnlyList<EventStreamEntry>>(afterSeq == 0 ? [entry] : []);
            });

        await harness.RunAsync(triggerReplay: true);

        Assert.Equal([0L, 0L, 1L], capturedAfterSequences);
        harness.ProjectionManager.Verify(
            m => m.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.ReplayState.Verify(s => s.SetFailed(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReplayCallback_BatchFailsOnEveryAttempt_ReplayFailsAfterTheBound()
    {
        var harness = new Harness(batchPipeline: FastRetryPipeline());
        var entry = NewEntry(sequenceNumber: 1);
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        harness.EventStreamRepository
            .Setup(r => r.GetManyAfterSequenceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry]);
        harness.ProjectionManager
            .Setup(m => m.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("row missing"));

        await harness.RunAsync(triggerReplay: true);

        harness.ProjectionManager.Verify(
            m => m.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(FastRetryAttempts));
        harness.ReplayState.Verify(s => s.SetFailed(It.Is<string>(m => m.Contains("row missing"))), Times.Once);
        harness.ReplayState.Verify(s => s.Deactivate(), Times.Once);
        Assert.Equal(FastRetryAttempts, harness.Logger.Entries.Count(e => e.EventId == LogEvents.Projection.ProjectionReplayBatchFailed));
    }

    [Fact]
    public async Task ReplayCallback_CancelledDuringABatch_IsNotRetriedAndNotRecordedAsFailure()
    {
        var harness = new Harness(batchPipeline: FastRetryPipeline());
        var entry = NewEntry(sequenceNumber: 1);
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        harness.EventStreamRepository
            .Setup(r => r.GetManyAfterSequenceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry]);
        harness.ProjectionManager
            .Setup(m => m.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await harness.RunAsync(triggerReplay: true);

        harness.ProjectionManager.Verify(
            m => m.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.ReplayState.Verify(s => s.SetFailed(It.IsAny<string>()), Times.Never);
        harness.ReplayState.Verify(s => s.Deactivate(), Times.Once);
        Assert.DoesNotContain(harness.Logger.Entries, e => e.EventId == LogEvents.Projection.ProjectionReplayBatchFailed);
    }

    private const int FastRetryAttempts = 5;

    private static ResiliencePipeline FastRetryPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = FastRetryAttempts - 1, Delay = TimeSpan.Zero })
            .Build();

    private static void SetupBatchSequence(Harness harness, IReadOnlyList<EventStreamEntry>[] batches)
    {
        var queue = new Queue<IReadOnlyList<EventStreamEntry>>(batches);
        harness.EventStreamRepository
            .Setup(r => r.GetManyAfterSequenceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(queue.Count > 0 ? queue.Dequeue() : (IReadOnlyList<EventStreamEntry>)[]));
    }

    private static EventStreamEntry NewEntry(long sequenceNumber, Guid? tenantId = null, Guid? actorTenantId = null) => new()
    {
        SequenceNumber = sequenceNumber,
        StreamId = Guid.NewGuid(),
        Version = 1,
        EventTypeName = "TestEvent",
        AggregateTypeName = "TestAggregate",
        DataJson = "{}",
        BucketId = 0,
        TenantId = tenantId ?? Guid.NewGuid(),
        ActorTenantId = actorTenantId ?? Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
    };

    private sealed class Harness
    {
        private readonly ResiliencePipeline _batchPipeline;

        public RecordingLogger<ProjectionReplayWorker> Logger { get; } = new();
        public Mock<IProjectionReplayState> ReplayState { get; } = new();
        public Mock<IProjectionViewTruncator> ViewTruncator { get; } = new();
        public Mock<IProjectionManager> ProjectionManager { get; } = new();
        public Mock<IWriteUnitOfWork> UnitOfWork { get; } = new();
        public Mock<ITransaction> Transaction { get; } = new();
        public Mock<IEventStreamRepository> EventStreamRepository { get; } = new();
        public Mock<IEventMapperFactory> EventMapperFactory { get; } = new();
        public Mock<ISessionContextProvider> SessionContextProvider { get; } = new();

        public Harness(ResiliencePipeline? batchPipeline = null)
        {
            _batchPipeline = batchPipeline ?? ResiliencePipeline.Empty;
            UnitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            UnitOfWork.Setup(u => u.CreateEventStreamRepository(It.IsAny<ITransaction>())).Returns(EventStreamRepository.Object);
            EventStreamRepository
                .Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);
            EventStreamRepository
                .Setup(r => r.GetManyAfterSequenceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            EventMapperFactory
                .Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<EventStreamEntry> _, CancellationToken _) => new List<IEvent> { Mock.Of<IEvent>() });
        }

        public async Task RunAsync(bool triggerReplay)
        {
            Func<Task>? capturedCallback = null;
            ReplayState
                .Setup(s => s.SubscribeToReplayRequestAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task>, CancellationToken>((cb, _) =>
                {
                    capturedCallback = cb;
                    return Task.CompletedTask;
                });

            var services = new ServiceCollection();
            services.AddSingleton(UnitOfWork.Object);
            services.AddSingleton(ViewTruncator.Object);
            services.AddSingleton(ProjectionManager.Object);
            services.AddSingleton(EventMapperFactory.Object);
            services.AddSingleton(SessionContextProvider.Object);
            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var pipelineProvider = new Mock<ResiliencePipelineProvider<string>>();
            pipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(_batchPipeline);

            var options = Options.Create(new ProjectionOptions { BatchSize = 100 });
            var worker = new ProjectionReplayWorker(
                Logger,
                scopeFactory,
                ReplayState.Object,
                pipelineProvider.Object,
                options);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);

            if (triggerReplay && capturedCallback is not null)
            {
                await capturedCallback();
            }

            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, int EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
    }
}
