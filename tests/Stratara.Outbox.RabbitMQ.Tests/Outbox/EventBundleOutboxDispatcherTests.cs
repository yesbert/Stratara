using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Stratara.Contracts.Messages;
using Stratara.Diagnostics;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Outbox.RabbitMQ.Tests.Diagnostics;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;

namespace Stratara.Outbox.RabbitMQ.Tests.Outbox;

public class EventBundleOutboxDispatcherTests
{
    private const string EventBundleTopic = "stratara.events";

    [Fact]
    public async Task EnqueueEventBundleAsync_DirectPublishSucceeds_DoesNotWriteToOutbox()
    {
        var harness = new Harness();
        var bundle = NewEventBundle();

        await harness.Sut.EnqueueEventBundleAsync(bundle);

        harness.MessageBus.Verify(
            b => b.PublishAsync(EventBundleTopic, bundle, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.UnitOfWork.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueEventBundleAsync_ReplayActive_BypassesBusAndWritesToOutbox()
    {
        var harness = new Harness();
        harness.ReplayState.Setup(s => s.IsReplayActive).Returns(true);
        var bundle = NewEventBundle();

        await harness.Sut.EnqueueEventBundleAsync(bundle);

        harness.MessageBus.Verify(
            b => b.PublishAsync(It.IsAny<string>(), It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.OutboxRepository.Verify(r => r.AddAsync(bundle, It.IsAny<CancellationToken>()), Times.Once);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueEventBundleAsync_BusPublishThrows_FallsBackToOutboxWrite()
    {
        var harness = new Harness();
        harness.MessageBus
            .Setup(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        var bundle = NewEventBundle();

        await harness.Sut.EnqueueEventBundleAsync(bundle);

        harness.OutboxRepository.Verify(r => r.AddAsync(bundle, It.IsAny<CancellationToken>()), Times.Once);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The save committed, and its caller is cancelled — the host is stopping — before the bus took the bundle. The
    /// bundle is recorded for the drain regardless of that cancellation, so no reader misses the committed events.
    /// </summary>
    [Fact]
    public async Task EnqueueEventBundleAsync_PublishCancelledByTheCaller_StillRecordsTheBundle()
    {
        var logger = new FakeLogger<EventBundleOutboxDispatcher>();
        var harness = new Harness(logger: logger);
        using var stop = new CancellationTokenSource();
        harness.MessageBus
            .Setup(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .Returns<string, EventBundle, CancellationToken>(async (_, _, ct) =>
            {
                await stop.CancelAsync();
                ct.ThrowIfCancellationRequested();
            });
        var bundle = NewEventBundle();

        await harness.Sut.EnqueueEventBundleAsync(bundle, stop.Token);

        harness.UnitOfWork.Verify(u => u.StartAsync(CancellationToken.None), Times.Once);
        harness.OutboxRepository.Verify(r => r.AddAsync(bundle, CancellationToken.None), Times.Once);
        harness.Transaction.Verify(t => t.SaveChangesAsync(CancellationToken.None), Times.Once);

        // Cut short, not failed: nothing to warn about.
        Assert.DoesNotContain(logger.Collector.GetSnapshot(), record => record.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_ReplayActive_ShortCircuits()
    {
        var harness = new Harness();
        harness.ReplayState.Setup(s => s.IsReplayActive).Returns(true);
        var entry = BuildOutboxEntry(NewEventBundle());

        await harness.Sut.EnqueueOutboxEntriesAsync([entry]);

        harness.UnitOfWork.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.MessageBus.Verify(
            b => b.PublishAsync(It.IsAny<string>(), It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_PublishSucceeds_DeletesEntry()
    {
        var harness = new Harness();
        var entry = BuildOutboxEntry(NewEventBundle());

        await harness.Sut.EnqueueOutboxEntriesAsync([entry]);

        harness.MessageBus.Verify(
            b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.OutboxRepository.Verify(r => r.DeleteAsync(entry.Id, It.IsAny<CancellationToken>()), Times.Once);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_PublishFails_KeepsEntry()
    {
        var harness = new Harness();
        harness.MessageBus
            .Setup(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        var entry = BuildOutboxEntry(NewEventBundle());

        await harness.Sut.EnqueueOutboxEntriesAsync([entry]);

        harness.OutboxRepository.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static EventBundle NewEventBundle() => new([], "{}");

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_PublishSucceeds_CountsWhatTheBusAccepted()
    {
        var harness = new Harness();

        var (listener, measurements) = MeterCapture.Start();
        using (listener)
        {
            await harness.Sut.EnqueueOutboxEntriesAsync(
                [BuildOutboxEntry(NewEventBundle()), BuildOutboxEntry(NewEventBundle())]);
        }

        Assert.Equal(2, PublishedCount(measurements.Snapshot()));
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_PublishFails_CountsNothing()
    {
        var harness = new Harness();
        harness.MessageBus
            .Setup(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));

        var (listener, measurements) = MeterCapture.Start();
        using (listener)
        {
            await harness.Sut.EnqueueOutboxEntriesAsync([BuildOutboxEntry(NewEventBundle())]);
        }

        Assert.Equal(0, PublishedCount(measurements.Snapshot()));
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_ReplayActive_CountsNothing()
    {
        var harness = new Harness();
        harness.ReplayState.Setup(s => s.IsReplayActive).Returns(true);

        var (listener, measurements) = MeterCapture.Start();
        using (listener)
        {
            await harness.Sut.EnqueueOutboxEntriesAsync([BuildOutboxEntry(NewEventBundle())]);
        }

        Assert.Equal(0, PublishedCount(measurements.Snapshot()));
    }

    /// <summary>
    /// <c>outbox-and-messaging</c> → a stored bundle that cannot be read is logged and left stored, and
    /// the drain continues with the bundles behind it rather than ending the pass on it.
    /// </summary>
    [Fact]
    public async Task EnqueueOutboxEntriesAsync_AnEntryThatCannotBeRead_IsLoggedAndKept_AndTheEntriesBehindItArePublished()
    {
        var logger = new FakeLogger<EventBundleOutboxDispatcher>();
        var harness = new Harness(logger: logger);
        var unreadable = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            DataJson = "{ not json",
            DataTypeName = typeof(EventBundle).AssemblyQualifiedName!,
            BucketId = 0,
            Timestamp = DateTimeOffset.UtcNow,
        };
        var readable = BuildOutboxEntry(NewEventBundle());

        await harness.Sut.EnqueueOutboxEntriesAsync([unreadable, readable], TestContext.Current.CancellationToken);

        harness.MessageBus.Verify(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.OutboxRepository.Verify(r => r.DeleteAsync(readable.Id, It.IsAny<CancellationToken>()), Times.Once);
        harness.OutboxRepository.Verify(r => r.DeleteAsync(unreadable.Id, It.IsAny<CancellationToken>()), Times.Never);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        var error = Assert.Single(logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Error);
        Assert.Equal(LogEvents.OutboxProcessing.OutboxEntryUnreadable, error.Id.Id);
        Assert.Contains(unreadable.Id.ToString(), error.Message, StringComparison.Ordinal);
    }

    private static double PublishedCount(IEnumerable<CapturedMeasurement> measurements) =>
        measurements
            .Where(m => m.Instrument == "outbox.published"
                        && Equals(m.Tags.GetValueOrDefault("outbox.kind"), "event"))
            .Sum(m => m.Value);

    private static OutboxEntry BuildOutboxEntry(EventBundle bundle) => new()
    {
        Id = Guid.NewGuid(),
        DataJson = JsonSerializer.Serialize(bundle),
        DataTypeName = typeof(EventBundle).AssemblyQualifiedName!,
        BucketId = 0,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private sealed class Harness
    {
        public Mock<IWriteUnitOfWork> UnitOfWork { get; } = new();
        public Mock<ITransaction> Transaction { get; } = new();
        public Mock<IOutboxRepository> OutboxRepository { get; } = new();
        public Mock<IMessageBus> MessageBus { get; } = new();
        public Mock<IMessagingIdentifier> MessagingIdentifier { get; } = new();
        public Mock<IProjectionReplayState> ReplayState { get; } = new();
        public Mock<ResiliencePipelineProvider<string>> PipelineProvider { get; } = new();

        public EventBundleOutboxDispatcher Sut { get; }

        public Harness(bool durableBundles = false, ILogger<EventBundleOutboxDispatcher>? logger = null, ResiliencePipeline? pipeline = null)
        {
            MessagingIdentifier.SetupGet(m => m.EventBundleTopic).Returns(EventBundleTopic);
            UnitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            UnitOfWork.Setup(u => u.CreateOutboxRepository(It.IsAny<ITransaction>())).Returns(OutboxRepository.Object);
            PipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(pipeline ?? ResiliencePipeline.Empty);

            Sut = new EventBundleOutboxDispatcher(
                logger ?? NullLogger<EventBundleOutboxDispatcher>.Instance,
                UnitOfWork.Object,
                MessageBus.Object,
                MessagingIdentifier.Object,
                PipelineProvider.Object,
                ReplayState.Object,
                Options.Create(new OutboxOptions { DurableBundles = durableBundles }));
        }
    }

    /// <summary>
    /// <c>outbox-and-messaging</c> → a pass attempts a stored message once. Retries inside a pass keep every message
    /// behind it waiting, and bundles already wait behind the pass's commands.
    /// </summary>
    [Fact]
    public async Task EnqueueOutboxEntriesAsync_PublishFails_AttemptsEachEntryOnce()
    {
        var harness = new Harness(pipeline: RetryThreeTimes());
        harness.MessageBus
            .Setup(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no subscriber bound"));

        await harness.Sut.EnqueueOutboxEntriesAsync([BuildOutboxEntry(NewEventBundle()), BuildOutboxEntry(NewEventBundle())]);

        harness.MessageBus.Verify(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        harness.OutboxRepository.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A refused bundle does not end the pass: each entry behind it gets its attempt.</summary>
    [Fact]
    public async Task EnqueueOutboxEntriesAsync_AnEntryIsRefused_TheEntriesBehindItArePublished()
    {
        var harness = new Harness(pipeline: RetryThreeTimes());
        harness.MessageBus
            .SetupSequence(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("refused"))
            .Returns(Task.CompletedTask);
        var refused = BuildOutboxEntry(NewEventBundle());
        var accepted = BuildOutboxEntry(NewEventBundle());

        await harness.Sut.EnqueueOutboxEntriesAsync([refused, accepted]);

        harness.OutboxRepository.Verify(r => r.DeleteAsync(accepted.Id, It.IsAny<CancellationToken>()), Times.Once);
        harness.OutboxRepository.Verify(r => r.DeleteAsync(refused.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary><c>resilience</c> → the dispatch policy still governs a first dispatch.</summary>
    [Fact]
    public async Task EnqueueEventBundleAsync_PublishFails_RetriesUnderThePipelineBeforeStoring()
    {
        var harness = new Harness(pipeline: RetryThreeTimes());
        harness.MessageBus
            .Setup(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        var bundle = NewEventBundle();

        await harness.Sut.EnqueueEventBundleAsync(bundle);

        harness.MessageBus.Verify(b => b.PublishAsync(EventBundleTopic, bundle, It.IsAny<CancellationToken>()), Times.Exactly(4));
        harness.OutboxRepository.Verify(r => r.AddAsync(bundle, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A durable bundle's first publication is a first dispatch too, though a copy is already stored.</summary>
    [Fact]
    public async Task DurableBundle_PublishFails_RetriesUnderThePipeline()
    {
        var harness = new Harness(durableBundles: true, pipeline: RetryThreeTimes());
        harness.MessageBus
            .Setup(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        var bundle = NewEventBundle();

        await harness.Sut.StoreEventBundleAsync(bundle, Mock.Of<ITransaction>());
        await harness.Sut.EnqueueEventBundleAsync(bundle);

        harness.MessageBus.Verify(b => b.PublishAsync(EventBundleTopic, bundle, It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    private static ResiliencePipeline RetryThreeTimes() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 3, Delay = TimeSpan.FromMilliseconds(1) })
            .Build();

    [Fact]
    public void StoresBundlesWithCommit_FollowsTheOption()
    {
        Assert.False(new Harness().Sut.StoresBundlesWithCommit);
        Assert.True(new Harness(durableBundles: true).Sut.StoresBundlesWithCommit);
    }

    [Fact]
    public async Task DurableBundle_StoredThenAccepted_IsRemovedByItsStoredIdentity()
    {
        var harness = new Harness(durableBundles: true);
        var bundle = NewEventBundle();
        var storeTransaction = Mock.Of<ITransaction>();
        Guid? storedId = null;
        harness.OutboxRepository
            .Setup(r => r.AddAsync(It.IsAny<Guid>(), bundle, It.IsAny<CancellationToken>()))
            .Callback<Guid, EventBundle, CancellationToken>((id, _, _) => storedId = id)
            .Returns(Task.CompletedTask);

        await harness.Sut.StoreEventBundleAsync(bundle, storeTransaction);
        await harness.Sut.EnqueueEventBundleAsync(bundle);

        harness.UnitOfWork.Verify(u => u.CreateOutboxRepository(storeTransaction), Times.Once);
        Assert.NotNull(storedId);
        harness.MessageBus.Verify(b => b.PublishAsync(EventBundleTopic, bundle, It.IsAny<CancellationToken>()), Times.Once);
        harness.OutboxRepository.Verify(r => r.DeleteAsync(storedId.Value, It.IsAny<CancellationToken>()), Times.Once);
        harness.OutboxRepository.Verify(r => r.AddAsync(bundle, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DurableBundle_BusRefuses_KeepsTheStoredRowAndDoesNotStoreTwice()
    {
        var harness = new Harness(durableBundles: true);
        harness.MessageBus
            .Setup(b => b.PublishAsync(EventBundleTopic, It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        var bundle = NewEventBundle();

        await harness.Sut.StoreEventBundleAsync(bundle, Mock.Of<ITransaction>());
        await harness.Sut.EnqueueEventBundleAsync(bundle);

        harness.OutboxRepository.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.OutboxRepository.Verify(r => r.AddAsync(bundle, It.IsAny<CancellationToken>()), Times.Never);
        harness.UnitOfWork.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DurableBundle_RemovalFails_TheSaveIsNotFailed()
    {
        var harness = new Harness(durableBundles: true);
        harness.OutboxRepository
            .Setup(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("delete failed"));
        var bundle = NewEventBundle();

        await harness.Sut.StoreEventBundleAsync(bundle, Mock.Of<ITransaction>());
        await harness.Sut.EnqueueEventBundleAsync(bundle);

        harness.MessageBus.Verify(b => b.PublishAsync(EventBundleTopic, bundle, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DurableBundle_ReplayActive_LeavesTheRowForTheDrain()
    {
        var harness = new Harness(durableBundles: true);
        harness.ReplayState.Setup(s => s.IsReplayActive).Returns(true);
        var bundle = NewEventBundle();

        await harness.Sut.StoreEventBundleAsync(bundle, Mock.Of<ITransaction>());
        await harness.Sut.EnqueueEventBundleAsync(bundle);

        harness.MessageBus.Verify(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.OutboxRepository.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.OutboxRepository.Verify(r => r.AddAsync(bundle, It.IsAny<CancellationToken>()), Times.Never);
    }
}
