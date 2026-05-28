using System.Text.Json;
using Microsoft.Extensions.Logging;
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
using Stratara.Resilience;

namespace Stratara.Infrastructure.Tests;

public class CommandOutboxDispatcherTests
{
    private static ResiliencePipeline CreateNoOpPipeline() => new ResiliencePipelineBuilder().Build();

    private static (CommandOutboxDispatcher sut,
        Mock<IOutboxRepository> repo,
        Mock<ITransaction> transaction,
        Mock<IWriteUnitOfWork> uow,
        Mock<IMessageBus> bus,
        Mock<IMessagingIdentifier> ids,
        Mock<ISessionContextProvider> session,
        Mock<ResiliencePipelineProvider<string>> provider) CreateSut()
    {
        var logger = new Mock<ILogger<CommandOutboxDispatcher>>();
        var repo = new Mock<IOutboxRepository>(MockBehavior.Strict);
        var transaction = new Mock<ITransaction>();
        var uow = new Mock<IWriteUnitOfWork>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);
        var ids = new Mock<IMessagingIdentifier>(MockBehavior.Strict);
        var session = new Mock<ISessionContextProvider>(MockBehavior.Strict);
        var provider = new Mock<ResiliencePipelineProvider<string>>(MockBehavior.Strict);
        var replayState = new Mock<IProjectionReplayState>();

        ids.SetupGet(i => i.CommandTopic).Returns("commands");
        session.SetupGet(s => s.Current).Returns(SessionContext.Empty());
        provider.Setup(p => p.GetPipeline(ResilienceNames.CommandDispatcher))
            .Returns(CreateNoOpPipeline());
        replayState.SetupGet(r => r.IsReplayActive).Returns(false);

        transaction.Setup(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);

        uow.Setup(u => u.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        uow.Setup(u => u.CreateOutboxRepository(transaction.Object))
            .Returns(repo.Object);

        var serializer = new Mock<ISecureJsonSerializer>();
        serializer.Setup(s => s.SerializeAsync(It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns<object, Guid?, Guid?, CancellationToken>((obj, _, _, _) => Task.FromResult(JsonSerializer.Serialize(obj)));

        var sut = new CommandOutboxDispatcher(logger.Object, uow.Object, bus.Object, ids.Object, session.Object, provider.Object, replayState.Object, serializer.Object);
        return (sut, repo, transaction, uow, bus, ids, session, provider);
    }

    [Fact]
    public async Task EnqueueCommandAsync_Sends_Immediately_On_Success()
    {
        var (sut, repo, transaction, _, bus, _, _, _) = CreateSut();

        bus.Setup(b => b.PublishAsync("commands", It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var id = await sut.EnqueueCommandAsync(new TestCommand("A"), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
        repo.Verify(r => r.AddAsync(It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        bus.Verify(b => b.PublishAsync("commands", It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueCommandAsync_Persists_When_Publish_Fails()
    {
        var (sut, repo, transaction, _, bus, _, _, _) = CreateSut();

        bus.Setup(b => b.PublishAsync("commands", It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fail"));

        repo.Setup(r => r.AddAsync(It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var id = await sut.EnqueueCommandAsync(new TestCommand("B"), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
        repo.Verify(r => r.AddAsync(It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_Deletes_On_Successful_Send_Only()
    {
        var (sut, repo, transaction, _, bus, _, _, _) = CreateSut();

        var okEntry = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            DataJson = JsonSerializer.Serialize(new CommandEnvelope(Guid.NewGuid(), "{}", typeof(TestCommand).AssemblyQualifiedName!, "{}")),
            DataTypeName = typeof(CommandEnvelope).AssemblyQualifiedName!,
            BucketId = 0,
            Timestamp = DateTimeOffset.UtcNow
        };

        var failEntry = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            DataJson = JsonSerializer.Serialize(new CommandEnvelope(Guid.NewGuid(), "{}", typeof(TestCommand).AssemblyQualifiedName!, "{}")),
            DataTypeName = typeof(CommandEnvelope).AssemblyQualifiedName!,
            BucketId = 0,
            Timestamp = DateTimeOffset.UtcNow
        };

        var sequence = new MockSequence();
        bus.InSequence(sequence)
            .Setup(b => b.PublishAsync("commands", It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        bus.InSequence(sequence)
            .Setup(b => b.PublishAsync("commands", It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        repo.Setup(r => r.DeleteAsync(okEntry.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.EnqueueOutboxEntriesAsync([okEntry, failEntry], CancellationToken.None);

        repo.Verify(r => r.DeleteAsync(okEntry.Id, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.DeleteAsync(failEntry.Id, It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ReSharper disable once NotAccessedPositionalProperty.Local
    private sealed record TestCommand(string Name) : ICommand;
}
