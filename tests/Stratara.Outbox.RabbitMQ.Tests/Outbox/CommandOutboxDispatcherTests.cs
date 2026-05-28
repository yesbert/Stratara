using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;

namespace Stratara.Outbox.RabbitMQ.Tests.Outbox;

public class CommandOutboxDispatcherTests
{
    private const string CommandTopic = "stratara.commands";

    public sealed record TestCommand(Guid Marker) : ICommand;

    [Fact]
    public async Task EnqueueCommandAsync_DirectPublishSucceeds_DoesNotWriteToOutbox()
    {
        var harness = new Harness();
        harness.SessionContext.Setup(s => s.Current).Returns(SessionContext.Empty());

        var id = await harness.Sut.EnqueueCommandAsync(new TestCommand(Guid.NewGuid()));

        Assert.NotEqual(Guid.Empty, id);
        harness.MessageBus.Verify(
            b => b.PublishAsync(CommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.UnitOfWork.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueCommandAsync_ReplayActive_BypassesBusAndWritesToOutbox()
    {
        var harness = new Harness();
        harness.SessionContext.Setup(s => s.Current).Returns(SessionContext.Empty());
        harness.ReplayState.Setup(s => s.IsReplayActive).Returns(true);

        await harness.Sut.EnqueueCommandAsync(new TestCommand(Guid.NewGuid()));

        harness.MessageBus.Verify(
            b => b.PublishAsync(It.IsAny<string>(), It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.OutboxRepository.Verify(
            r => r.AddAsync(It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueCommandAsync_BusPublishThrows_FallsBackToOutboxWrite()
    {
        var harness = new Harness();
        harness.SessionContext.Setup(s => s.Current).Returns(SessionContext.Empty());
        harness.MessageBus
            .Setup(b => b.PublishAsync(CommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));

        await harness.Sut.EnqueueCommandAsync(new TestCommand(Guid.NewGuid()));

        harness.OutboxRepository.Verify(
            r => r.AddAsync(It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueCommandAsync_NullSessionContext_Throws()
    {
        var harness = new Harness();
        harness.SessionContext.Setup(s => s.Current).Returns((SessionContext?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.EnqueueCommandAsync(new TestCommand(Guid.NewGuid())));
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_ReplayActive_ShortCircuits()
    {
        var harness = new Harness();
        harness.ReplayState.Setup(s => s.IsReplayActive).Returns(true);

        await harness.Sut.EnqueueOutboxEntriesAsync([BuildOutboxEntry(new CommandEnvelope(Guid.NewGuid(), "{}", "T", "{}"))]);

        harness.UnitOfWork.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.MessageBus.Verify(
            b => b.PublishAsync(It.IsAny<string>(), It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_PublishSucceeds_DeletesEntry()
    {
        var harness = new Harness();
        var envelope = new CommandEnvelope(Guid.NewGuid(), "{}", "T", "{}");
        var entry = BuildOutboxEntry(envelope);

        await harness.Sut.EnqueueOutboxEntriesAsync([entry]);

        harness.MessageBus.Verify(
            b => b.PublishAsync(CommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.OutboxRepository.Verify(r => r.DeleteAsync(entry.Id, It.IsAny<CancellationToken>()), Times.Once);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_PublishFails_KeepsEntry()
    {
        var harness = new Harness();
        harness.MessageBus
            .Setup(b => b.PublishAsync(CommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        var envelope = new CommandEnvelope(Guid.NewGuid(), "{}", "T", "{}");
        var entry = BuildOutboxEntry(envelope);

        await harness.Sut.EnqueueOutboxEntriesAsync([entry]);

        harness.OutboxRepository.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static OutboxEntry BuildOutboxEntry(CommandEnvelope envelope) => new()
    {
        Id = Guid.NewGuid(),
        DataJson = JsonSerializer.Serialize(envelope),
        DataTypeName = typeof(CommandEnvelope).AssemblyQualifiedName!,
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
        public Mock<ISessionContextProvider> SessionContext { get; } = new();
        public Mock<IProjectionReplayState> ReplayState { get; } = new();
        public Mock<ResiliencePipelineProvider<string>> PipelineProvider { get; } = new();

        public CommandOutboxDispatcher Sut { get; }

        public Harness()
        {
            MessagingIdentifier.SetupGet(m => m.CommandTopic).Returns(CommandTopic);
            UnitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            UnitOfWork.Setup(u => u.CreateOutboxRepository(It.IsAny<ITransaction>())).Returns(OutboxRepository.Object);
            PipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);

            var serializer = new Mock<ISecureJsonSerializer>();
            serializer.Setup(s => s.SerializeAsync(It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .Returns<object, Guid?, Guid?, CancellationToken>((obj, _, _, _) => Task.FromResult(JsonSerializer.Serialize(obj)));

            Sut = new CommandOutboxDispatcher(
                NullLogger<CommandOutboxDispatcher>.Instance,
                UnitOfWork.Object,
                MessageBus.Object,
                MessagingIdentifier.Object,
                SessionContext.Object,
                PipelineProvider.Object,
                ReplayState.Object,
                serializer.Object);
        }
    }
}
