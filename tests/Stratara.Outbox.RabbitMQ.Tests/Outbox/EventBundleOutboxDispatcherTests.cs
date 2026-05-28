using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Outbox.RabbitMQ.Outbox;
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

        public Harness()
        {
            MessagingIdentifier.SetupGet(m => m.EventBundleTopic).Returns(EventBundleTopic);
            UnitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            UnitOfWork.Setup(u => u.CreateOutboxRepository(It.IsAny<ITransaction>())).Returns(OutboxRepository.Object);
            PipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);

            Sut = new EventBundleOutboxDispatcher(
                NullLogger<EventBundleOutboxDispatcher>.Instance,
                UnitOfWork.Object,
                MessageBus.Object,
                MessagingIdentifier.Object,
                PipelineProvider.Object,
                ReplayState.Object);
        }
    }
}
