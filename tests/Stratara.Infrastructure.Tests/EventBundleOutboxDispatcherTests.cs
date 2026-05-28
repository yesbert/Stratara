using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Resilience;

namespace Stratara.Infrastructure.Tests;

public class EventBundleOutboxDispatcherTests
{
    private static ResiliencePipeline CreateNoOpPipeline() => new ResiliencePipelineBuilder().Build();

    private static (EventBundleOutboxDispatcher sut,
        Mock<IOutboxRepository> repo,
        Mock<ITransaction> transaction,
        Mock<IWriteUnitOfWork> uow,
        Mock<IMessageBus> bus,
        Mock<IMessagingIdentifier> ids,
        Mock<ResiliencePipelineProvider<string>> provider) CreateSut()
    {
        var logger = new Mock<ILogger<EventBundleOutboxDispatcher>>();
        var repo = new Mock<IOutboxRepository>(MockBehavior.Strict);
        var transaction = new Mock<ITransaction>();
        var uow = new Mock<IWriteUnitOfWork>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);
        var ids = new Mock<IMessagingIdentifier>(MockBehavior.Strict);
        var provider = new Mock<ResiliencePipelineProvider<string>>(MockBehavior.Strict);
        var replayState = new Mock<IProjectionReplayState>();

        ids.SetupGet(i => i.EventBundleTopic).Returns("events");
        provider.Setup(p => p.GetPipeline(ResilienceNames.EventBundleDispatcher))
            .Returns(CreateNoOpPipeline());
        replayState.SetupGet(r => r.IsReplayActive).Returns(false);

        transaction.Setup(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);

        uow.Setup(u => u.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        uow.Setup(u => u.CreateOutboxRepository(transaction.Object))
            .Returns(repo.Object);

        var sut = new EventBundleOutboxDispatcher(logger.Object, uow.Object, bus.Object, ids.Object, provider.Object, replayState.Object);
        return (sut, repo, transaction, uow, bus, ids, provider);
    }

    [Fact]
    public async Task EnqueueEventBundleAsync_Sends_Immediately_On_Success()
    {
        var (sut, repo, transaction, _, bus, _, _) = CreateSut();

        var bundle = new EventBundle([], "{}");

        bus.Setup(b => b.PublishAsync("events", bundle, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.EnqueueEventBundleAsync(bundle, CancellationToken.None);

        repo.Verify(r => r.AddAsync(It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        bus.Verify(b => b.PublishAsync("events", bundle, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueEventBundleAsync_Persists_When_Publish_Fails()
    {
        var (sut, repo, transaction, _, bus, _, _) = CreateSut();

        var bundle = new EventBundle([], "{}");

        bus.Setup(b => b.PublishAsync("events", bundle, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("x"));

        repo.Setup(r => r.AddAsync(bundle, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await sut.EnqueueEventBundleAsync(bundle, CancellationToken.None);

        repo.Verify(r => r.AddAsync(bundle, It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_Deletes_On_Successful_Send_Only()
    {
        var (sut, repo, transaction, _, bus, _, _) = CreateSut();

        var okEnvelope = new EventBundle([], "{}");
        var failEnvelope = new EventBundle([], "{}");

        var okEntry = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            DataJson = JsonSerializer.Serialize(okEnvelope),
            DataTypeName = typeof(EventBundle).AssemblyQualifiedName!,
            BucketId = 0,
            Timestamp = DateTimeOffset.UtcNow
        };

        var failEntry = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            DataJson = JsonSerializer.Serialize(failEnvelope),
            DataTypeName = typeof(EventBundle).AssemblyQualifiedName!,
            BucketId = 0,
            Timestamp = DateTimeOffset.UtcNow
        };

        var seq = new MockSequence();
        bus.InSequence(seq).Setup(b => b.PublishAsync("events", It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        bus.InSequence(seq).Setup(b => b.PublishAsync("events", It.IsAny<EventBundle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        repo.Setup(r => r.DeleteAsync(okEntry.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.EnqueueOutboxEntriesAsync([okEntry, failEntry], CancellationToken.None);

        repo.Verify(r => r.DeleteAsync(okEntry.Id, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.DeleteAsync(failEntry.Id, It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
