using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Outbox.RabbitMQ.Tests.Diagnostics;
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
    private const string HeavyCommandTopic = "stratara.heavy-commands";

    public sealed record TestCommand(Guid Marker) : ICommand;

    public sealed record HeavyTestCommand(Guid Marker) : ICommand, IHeavyCommand;

    public sealed record OwnedTestCommand(Guid Tenant, Guid? User) : ICommand, INamesDataOwner
    {
        Guid INamesDataOwner.DataOwnerTenantId => Tenant;

        Guid? INamesDataOwner.DataOwnerUserId => User;
    }

    /// <summary>
    /// <c>outbox-and-messaging</c> → a command that names the owner of its protected fields carries
    /// that owner over the bus, beside its session.
    /// </summary>
    [Fact]
    public async Task EnqueueCommandAsync_CommandNamingAnOwner_PublishesAnEnvelopeCarryingIt()
    {
        var harness = new Harness();
        var session = SessionContext.Empty();
        harness.SessionContext.Setup(s => s.Current).Returns(session);
        var published = harness.CapturePublished();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        await harness.Sut.EnqueueCommandAsync(new OwnedTestCommand(tenant, user));

        var envelope = Assert.Single(published);
        Assert.Equal(tenant, envelope.DataOwnerTenantId);
        Assert.Equal(user, envelope.DataOwnerUserId);
        Assert.Equal(session, JsonSerializer.Deserialize<SessionContext>(envelope.SessionContextJson));
    }

    [Fact]
    public async Task EnqueueCommandAsync_CommandNamingAnOwner_StoresAnEnvelopeCarryingItWhenTheBusIsDown()
    {
        var harness = new Harness();
        harness.SessionContext.Setup(s => s.Current).Returns(SessionContext.Empty());
        harness.MessageBus
            .Setup(b => b.PublishAsync(CommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        var tenant = Guid.NewGuid();

        await harness.Sut.EnqueueCommandAsync(new OwnedTestCommand(tenant, null));

        harness.OutboxRepository.Verify(
            r => r.AddAsync(It.Is<CommandEnvelope>(e => e.DataOwnerTenantId == tenant && e.DataOwnerUserId == null), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EnqueueCommandAsync_CommandNamingNone_PublishesAnEnvelopeWithoutAnOwner()
    {
        var harness = new Harness();
        harness.SessionContext.Setup(s => s.Current).Returns(SessionContext.Empty());
        var published = harness.CapturePublished();

        await harness.Sut.EnqueueCommandAsync(new TestCommand(Guid.NewGuid()));

        var envelope = Assert.Single(published);
        Assert.Null(envelope.DataOwnerTenantId);
        Assert.DoesNotContain("DataOwner", JsonSerializer.Serialize(envelope));
    }

    [Fact]
    public async Task EnqueueCommandAsync_CommandNamingAnEmptyTenant_ThrowsAndSendsNothing()
    {
        var harness = new Harness();
        harness.SessionContext.Setup(s => s.Current).Returns(SessionContext.Empty());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Sut.EnqueueCommandAsync(new OwnedTestCommand(Guid.Empty, null)));

        Assert.Contains(nameof(OwnedTestCommand), exception.Message);
        harness.MessageBus.Verify(
            b => b.PublishAsync(It.IsAny<string>(), It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.OutboxRepository.Verify(r => r.AddAsync(It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The drain republishes what the row holds. A stored command that names an owner is republished
    /// with it and with the signature it was stored under, which still covers it.
    /// </summary>
    [Fact]
    public async Task EnqueueOutboxEntriesAsync_StoredCommandNamingAnOwner_IsRepublishedWithItsOwner()
    {
        var harness = new Harness();
        var published = harness.CapturePublished();
        var unsigned = new CommandEnvelope(Guid.NewGuid(), "{}", typeof(OwnedTestCommand).AssemblyQualifiedName!, "{}")
        {
            DataOwnerTenantId = Guid.NewGuid(),
            DataOwnerUserId = Guid.NewGuid()
        };
        var stored = unsigned with { Signature = "sig:" + BusEnvelopeCanonical.Of(unsigned) };

        await harness.Sut.EnqueueOutboxEntriesAsync([BuildOutboxEntry(stored)]);

        var republished = Assert.Single(published);
        Assert.Equal(stored, republished);
        Assert.Equal(stored.Signature, "sig:" + BusEnvelopeCanonical.Of(republished));
    }

    [Fact]
    public async Task EnqueueCommandAsync_HeavyCommand_PublishesToHeavyTopic()
    {
        var harness = new Harness();
        harness.SessionContext.Setup(s => s.Current).Returns(SessionContext.Empty());

        await harness.Sut.EnqueueCommandAsync(new HeavyTestCommand(Guid.NewGuid()));

        harness.MessageBus.Verify(
            b => b.PublishAsync(HeavyCommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.MessageBus.Verify(
            b => b.PublishAsync(CommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

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

        var exception = await Assert.ThrowsAsync<SessionRequiredException>(
            () => harness.Sut.EnqueueCommandAsync(new TestCommand(Guid.NewGuid())));
        Assert.Equal("Session context is not set", exception.Message);
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

    [Fact]
    public async Task EnqueueCommandAsync_HeavyCommand_FallingBackToStorage_RecordsTheHeavyLane()
    {
        var harness = new Harness();
        harness.SessionContext.Setup(s => s.Current).Returns(SessionContext.Empty());
        harness.MessageBus
            .Setup(b => b.PublishAsync(HeavyCommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        CommandEnvelope? stored = null;
        harness.OutboxRepository
            .Setup(r => r.AddAsync(It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<CommandEnvelope, CancellationToken>((envelope, _) => stored = envelope)
            .Returns(Task.CompletedTask);

        await harness.Sut.EnqueueCommandAsync(new HeavyTestCommand(Guid.NewGuid()));

        Assert.NotNull(stored);
        Assert.True(stored.Heavy);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_StoredHeavyCommand_WithUnresolvableType_RepublishesToTheHeavyTopic()
    {
        var harness = new Harness();
        var envelope = new CommandEnvelope(Guid.NewGuid(), "{}", "Contoso.Unregistered, Contoso", "{}", Heavy: true);

        await harness.Sut.EnqueueOutboxEntriesAsync([BuildOutboxEntry(envelope)]);

        harness.MessageBus.Verify(
            b => b.PublishAsync(HeavyCommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.MessageBus.Verify(
            b => b.PublishAsync(CommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_StoredOrdinaryCommand_WithUnresolvableType_RepublishesToTheSharedTopic()
    {
        var harness = new Harness();
        var envelope = new CommandEnvelope(Guid.NewGuid(), "{}", "Contoso.Unregistered, Contoso", "{}");

        await harness.Sut.EnqueueOutboxEntriesAsync([BuildOutboxEntry(envelope)]);

        harness.MessageBus.Verify(
            b => b.PublishAsync(CommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.MessageBus.Verify(
            b => b.PublishAsync(HeavyCommandTopic, It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_PublishSucceeds_CountsWhatTheBusAccepted()
    {
        var harness = new Harness();
        var entries = new[]
        {
            BuildOutboxEntry(new CommandEnvelope(Guid.NewGuid(), "{}", "T", "{}")),
            BuildOutboxEntry(new CommandEnvelope(Guid.NewGuid(), "{}", "T", "{}")),
        };

        var (listener, measurements) = MeterCapture.Start();
        using (listener)
        {
            await harness.Sut.EnqueueOutboxEntriesAsync(entries);
        }

        Assert.Equal(2, PublishedCount(measurements.Snapshot()));
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_SomePublishesFail_CountsOnlyTheAcceptedOnes()
    {
        var harness = new Harness();
        var accepted = new CommandEnvelope(Guid.NewGuid(), "{}", "T", "{}");
        var rejected = new CommandEnvelope(Guid.NewGuid(), "{}", "T", "{}");
        harness.MessageBus
            .Setup(b => b.PublishAsync(CommandTopic, It.Is<CommandEnvelope>(e => e.Id == rejected.Id), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));

        var (listener, measurements) = MeterCapture.Start();
        using (listener)
        {
            await harness.Sut.EnqueueOutboxEntriesAsync([BuildOutboxEntry(accepted), BuildOutboxEntry(rejected)]);
        }

        Assert.Equal(1, PublishedCount(measurements.Snapshot()));
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_ReplayActive_CountsNothing()
    {
        var harness = new Harness();
        harness.ReplayState.Setup(s => s.IsReplayActive).Returns(true);

        var (listener, measurements) = MeterCapture.Start();
        using (listener)
        {
            await harness.Sut.EnqueueOutboxEntriesAsync([BuildOutboxEntry(new CommandEnvelope(Guid.NewGuid(), "{}", "T", "{}"))]);
        }

        Assert.Equal(0, PublishedCount(measurements.Snapshot()));
    }

    private static double PublishedCount(IEnumerable<CapturedMeasurement> measurements) =>
        measurements
            .Where(m => m.Instrument == "outbox.published"
                        && Equals(m.Tags.GetValueOrDefault("outbox.kind"), "command"))
            .Sum(m => m.Value);

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

        public List<CommandEnvelope> CapturePublished()
        {
            var published = new List<CommandEnvelope>();
            MessageBus
                .Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
                .Callback<string, CommandEnvelope, CancellationToken>((_, envelope, _) => published.Add(envelope))
                .Returns(Task.CompletedTask);
            return published;
        }

        public Harness()
        {
            MessagingIdentifier.SetupGet(m => m.CommandTopic).Returns(CommandTopic);
            MessagingIdentifier.SetupGet(m => m.HeavyCommandTopic).Returns(HeavyCommandTopic);
            MessagingIdentifier.Setup(m => m.GetCommandTopic(It.IsAny<Type>()))
                .Returns<Type>(t => IMessagingIdentifier.IsHeavy(t) ? HeavyCommandTopic : CommandTopic);
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
