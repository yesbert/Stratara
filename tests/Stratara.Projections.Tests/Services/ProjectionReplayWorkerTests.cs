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
using Stratara.Domain;

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
            .Setup(r => r.GetManyAfterSequenceInStreamOrderAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
    public async Task ReplayCallback_ReorderedBatch_IsAppliedAsReturnedAndTheNextStartsAfterItsHighestSequence()
    {
        var harness = new Harness();
        var first = NewEntry(sequenceNumber: 22);
        var second = NewEntry(sequenceNumber: 21);
        var third = NewEntry(sequenceNumber: 20);
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(22);

        var capturedAfterSequences = new List<long>();
        harness.EventStreamRepository
            .Setup(r => r.GetManyAfterSequenceInStreamOrderAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<long, int, CancellationToken>((afterSeq, _, _) =>
            {
                capturedAfterSequences.Add(afterSeq);
                return Task.FromResult<IReadOnlyList<EventStreamEntry>>(afterSeq == 0 ? [first, second, third] : []);
            });
        var applied = new List<long>();
        harness.EventMapperFactory
            .Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<EventStreamEntry> entries, CancellationToken _) =>
            {
                applied.AddRange(entries.Select(e => e.SequenceNumber));
                return new List<IEvent> { Mock.Of<IEvent>() };
            });

        await harness.RunAsync(triggerReplay: true);

        Assert.Equal([22L, 21L, 20L], applied);
        Assert.Equal([0L, 22L], capturedAfterSequences);
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
            .Setup(r => r.GetManyAfterSequenceInStreamOrderAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.GetManyAfterSequenceInStreamOrderAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.GetManyAfterSequenceInStreamOrderAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.GetManyAfterSequenceInStreamOrderAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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

    [Fact]
    public async Task ReplayCallback_EmptiesTheRecordOfEachDeclaringProjectionBeforeTruncating()
    {
        var harness = new Harness();
        var order = new List<string>();
        harness.ViewTruncator.Setup(t => t.TruncateAllAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("truncate")).Returns(Task.CompletedTask);
        var store = new Mock<IForgottenTenantStore>();
        store.Setup(s => s.ClearAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string projection, CancellationToken _) => order.Add($"clear {projection}")).Returns(Task.CompletedTask);
        harness.Configure = services =>
        {
            services.AddSingleton(store.Object);
            services.AddScoped<IProjection, EntryProjection>();
            services.AddScoped<IProjection, UndeclaredProjection>();
            services.AddScoped<IProjectionHandler>(_ => new ProjectionHandler(new ProjectionMethodInvoker(), null, store.Object));
        };

        await harness.RunAsync(triggerReplay: true);

        Assert.Equal([$"clear {nameof(EntryProjection)}", "truncate"], order);
        harness.ReplayState.Verify(s => s.SetFailed(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReplayCallback_WithoutADeclaringProjection_DoesNotTouchTheStore()
    {
        var harness = new Harness();
        var store = new Mock<IForgottenTenantStore>(MockBehavior.Strict);
        harness.Configure = services =>
        {
            services.AddSingleton(store.Object);
            services.AddScoped<IProjection, UndeclaredProjection>();
        };

        await harness.RunAsync(triggerReplay: true);

        harness.ViewTruncator.Verify(t => t.TruncateAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.ReplayState.Verify(s => s.SetFailed(It.IsAny<string>()), Times.Never);
    }

    private sealed class UndeclaredProjection : IProjection
    {
        private Task HandleAsync(EntryCreated @event, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ReplayCallback_ReplaysPastAFactRecordedAfterItsTenantWasDeleted()
    {
        var harness = new Harness();
        var tenant = Guid.NewGuid();
        var entry = Guid.NewGuid();
        var created = NewEntry(sequenceNumber: 1, tenantId: tenant);
        var cascade = NewEntry(sequenceNumber: 2);
        var indexed = NewEntry(sequenceNumber: 3, tenantId: tenant);
        var events = new Dictionary<EventStreamEntry, IEvent>(ReferenceEqualityComparer.Instance)
        {
            [created] = new Event<EntryCreated>(created.Id, 1, new EntryCreated(), entry, tenant, Guid.Empty),
            [cascade] = new Event<CustomerTenantsDeleted>(cascade.Id, 1,
                new CustomerTenantsDeleted(cascade.StreamId, [tenant], DateTimeOffset.UtcNow), cascade.StreamId, cascade.TenantId, Guid.Empty),
            [indexed] = new Event<EntryIndexed>(indexed.Id, 2, new EntryIndexed(), entry, tenant, Guid.Empty)
        };
        harness.EventStreamRepository.Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        SetupBatchSequence(harness, [[created, cascade, indexed], []]);
        harness.EventMapperFactory
            .Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<EventStreamEntry> entries, CancellationToken _) => entries.Select(e => events[e]).ToList());
        var projection = new EntryProjection();
        var store = new InMemoryForgottenTenantStore();
        harness.Configure = services => services.AddScoped<IProjectionManager>(_ => new ProjectionManager(
            Mock.Of<ILogger<ProjectionManager>>(), new ProjectionHandler(new ProjectionMethodInvoker(), null, store), [projection]));

        await harness.RunAsync(triggerReplay: true);

        harness.ReplayState.Verify(s => s.SetFailed(It.IsAny<string>()), Times.Never);
        Assert.Empty(projection.Rows);
        Assert.Contains((nameof(EntryProjection), tenant), store.Forgotten);
    }

    private sealed record EntryCreated;

    private sealed record EntryIndexed;

    private sealed class EntryProjection : IForgetsDeletedTenants
    {
        public Dictionary<Guid, Guid> Rows { get; } = [];

        private Task HandleAsync(IEvent<EntryCreated> @event, CancellationToken cancellationToken)
        {
            Rows[@event.StreamId] = @event.TenantId;
            return Task.CompletedTask;
        }

        private Task HandleAsync(IEvent<EntryIndexed> @event, CancellationToken cancellationToken) =>
            Rows.ContainsKey(@event.StreamId)
                ? Task.CompletedTask
                : throw new PrecedingFactMissingException(@event.StreamId, nameof(EntryIndexed));

        private Task HandleAsync(CustomerTenantsDeleted @event, CancellationToken cancellationToken)
        {
            foreach (var row in Rows.Where(r => @event.TenantIds.Contains(r.Value)).ToList())
            {
                Rows.Remove(row.Key);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryForgottenTenantStore : IForgottenTenantStore
    {
        public HashSet<(string Projection, Guid TenantId)> Forgotten { get; } = [];

        public Task ForgetAsync(string projection, IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken = default)
        {
            Forgotten.UnionWith(tenantIds.Select(t => (projection, t)));
            return Task.CompletedTask;
        }

        public Task<bool> HasForgottenAsync(string projection, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Forgotten.Contains((projection, tenantId)));

        public Task ClearAsync(string projection, CancellationToken cancellationToken = default)
        {
            Forgotten.RemoveWhere(f => f.Projection == projection);
            return Task.CompletedTask;
        }

    }

    private static void SetupBatchSequence(Harness harness, IReadOnlyList<EventStreamEntry>[] batches)
    {
        var queue = new Queue<IReadOnlyList<EventStreamEntry>>(batches);
        harness.EventStreamRepository
            .Setup(r => r.GetManyAfterSequenceInStreamOrderAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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

        public Action<IServiceCollection>? Configure { get; set; }

        public Harness(ResiliencePipeline? batchPipeline = null)
        {
            _batchPipeline = batchPipeline ?? ResiliencePipeline.Empty;
            UnitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            UnitOfWork.Setup(u => u.CreateEventStreamRepository(It.IsAny<ITransaction>())).Returns(EventStreamRepository.Object);
            EventStreamRepository
                .Setup(r => r.GetMaxSequenceNumberAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);
            EventStreamRepository
                .Setup(r => r.GetManyAfterSequenceInStreamOrderAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
            Configure?.Invoke(services);
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
