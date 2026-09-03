using System.Text.Json;
using Stratara.Outbox.RabbitMQ.Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Mediator;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Diagnostics;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Shared.Reflections;

using Stratara.Outbox.RabbitMQ.Tests.Diagnostics;

namespace Stratara.Outbox.RabbitMQ.Tests.Mediator;

public class MediatorCommandWorkerTests
{
    public sealed record PlainCommand(string Payload) : ICommand;

    public sealed record AggregateCommand(Guid AggregateId, string Payload) : ICommand, IAggregateScopedCommand;

    [Fact]
    public async Task DispatchAsync_PlainCommand_RestoresSessionContextAndDispatchesViaMediator()
    {
        var harness = new Harness();
        var command = new PlainCommand("hello");
        var session = SessionContext.Empty();
        var envelope = NewEnvelope(command, session);

        await harness.Sut.DispatchAsync(envelope, CancellationToken.None);

        harness.SessionContextProvider.Verify(
            s => s.Set(It.Is<SessionContext>(c => c.CorrelationId == session.CorrelationId)),
            Times.Once);
        harness.Mediator.Verify(
            m => m.HandleAsync(It.Is<PlainCommand>(c => c.Payload == "hello"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_RecordsCommandDuration_TaggedWithTypeAndOutcome()
    {
        var harness = new Harness();
        var envelope = NewEnvelope(new PlainCommand("hello"), SessionContext.Empty());
        var (listener, measurements) = MeterCapture.Start();

        using (listener)
        {
            await harness.Sut.DispatchAsync(envelope, CancellationToken.None);
        }

        var recorded = measurements.Snapshot()
            .Where(m => m.Instrument == "command.duration")
            .Where(m => Equals(m.Tags.GetValueOrDefault("request.type"), nameof(PlainCommand)))
            .ToList();

        var measurement = Assert.Single(recorded);
        Assert.Equal("success", measurement.Tags.GetValueOrDefault("outcome"));
    }

    [Fact]
    public async Task DispatchAsync_RecordsCommandDuration_AsFailureWhenTheHandlerThrows()
    {
        var harness = new Harness();
        harness.Mediator
            .Setup(m => m.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("handler blew up"));

        var envelope = NewEnvelope(new PlainCommand("hello"), SessionContext.Empty());
        var (listener, measurements) = MeterCapture.Start();

        using (listener)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => harness.Sut.DispatchAsync(envelope, CancellationToken.None));
        }

        var recorded = measurements.Snapshot()
            .Where(m => m.Instrument == "command.duration")
            .Where(m => Equals(m.Tags.GetValueOrDefault("request.type"), nameof(PlainCommand)))
            .ToList();

        var measurement = Assert.Single(recorded);
        Assert.Equal("failure", measurement.Tags.GetValueOrDefault("outcome"));
    }

    [Fact]
    public async Task DispatchAsync_AggregateScopedCommand_DispatchesViaMediator()
    {
        var harness = new Harness();
        var command = new AggregateCommand(Guid.NewGuid(), "ag");
        var envelope = NewEnvelope(command, SessionContext.Empty());

        await harness.Sut.DispatchAsync(envelope, CancellationToken.None);

        harness.Mediator.Verify(
            m => m.HandleAsync(It.Is<AggregateCommand>(c => c.Payload == "ag"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_UnknownCommandType_ThrowsInvalidOperationException()
    {
        var harness = new Harness();
        var envelope = new CommandEnvelope(
            Guid.NewGuid(),
            "{}",
            "Stratara.NotARealCommand, Stratara.NotARealAssembly",
            JsonSerializer.Serialize(SessionContext.Empty()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.DispatchAsync(envelope, CancellationToken.None));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public async Task DispatchAsync_InvalidCommandJson_ThrowsInvalidOperationException()
    {
        var harness = new Harness();
        var envelope = new CommandEnvelope(
            Guid.NewGuid(),
            "null",
            typeof(PlainCommand).GetQualifiedTypeName(),
            JsonSerializer.Serialize(SessionContext.Empty()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.DispatchAsync(envelope, CancellationToken.None));
    }

    [Fact]
    public async Task DispatchAsync_InvalidSessionContextJson_ThrowsInvalidOperationException()
    {
        var harness = new Harness();
        var command = new PlainCommand("x");
        var envelope = new CommandEnvelope(
            Guid.NewGuid(),
            JsonSerializer.Serialize(command),
            typeof(PlainCommand).GetQualifiedTypeName(),
            "null");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.DispatchAsync(envelope, CancellationToken.None));
    }

    [Fact]
    public async Task DispatchAsync_StrictMode_ValidSignature_Dispatches()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Strict, signer);
        var command = new PlainCommand("hello");
        var unsigned = NewEnvelope(command, SessionContext.Empty());
        var envelope = unsigned with { Signature = signer.Sign(BusEnvelopeCanonical.Of(unsigned)) };

        await harness.Sut.DispatchAsync(envelope, CancellationToken.None);

        harness.Mediator.Verify(m => m.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_StrictMode_MissingSignature_Throws()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Strict, signer);
        var envelope = NewEnvelope(new PlainCommand("hello"), SessionContext.Empty());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.DispatchAsync(envelope, CancellationToken.None));

        Assert.Contains("carries no signature", ex.Message);
        Assert.DoesNotContain("does not verify", ex.Message);
        harness.Mediator.Verify(m => m.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        var entry = Assert.Single(harness.Logger.Collector.GetSnapshot());
        Assert.Equal(LogEvents.CommandProcessing.CommandEnvelopeUnsignedRejected, entry.Id.Id);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public async Task DispatchAsync_StrictMode_TamperedSessionContext_Throws()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Strict, signer);
        var command = new PlainCommand("hello");
        var unsigned = NewEnvelope(command, SessionContext.Empty());
        var signed = unsigned with { Signature = signer.Sign(BusEnvelopeCanonical.Of(unsigned)) };
        var tampered = signed with { SessionContextJson = JsonSerializer.Serialize(SessionContext.Empty() with { TenantId = Guid.NewGuid() }) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.DispatchAsync(tampered, CancellationToken.None));

        Assert.Contains("does not verify", ex.Message);
        Assert.DoesNotContain("carries no signature", ex.Message);
        var entry = Assert.Single(harness.Logger.Collector.GetSnapshot());
        Assert.Equal(LogEvents.CommandProcessing.CommandEnvelopeIntegrityRejected, entry.Id.Id);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public async Task DispatchAsync_PermissiveMode_MissingSignature_DispatchesAndRecordsUnsigned()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Permissive, signer);
        var envelope = NewEnvelope(new PlainCommand("hello"), SessionContext.Empty());

        await harness.Sut.DispatchAsync(envelope, CancellationToken.None);

        harness.Mediator.Verify(m => m.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        var entry = Assert.Single(harness.Logger.Collector.GetSnapshot(), e => e.Level == LogLevel.Warning);
        Assert.Equal(LogEvents.CommandProcessing.CommandEnvelopeUnsignedWarning, entry.Id.Id);
        Assert.Contains("carries no signature", entry.Message);
        Assert.DoesNotContain(harness.Logger.Collector.GetSnapshot(), e => e.Id.Id == LogEvents.CommandProcessing.CommandEnvelopeIntegrityWarning);
    }

    [Fact]
    public async Task DispatchAsync_PermissiveMode_InvalidSignature_DispatchesAndRecordsInvalid()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Permissive, signer);
        var envelope = NewEnvelope(new PlainCommand("hello"), SessionContext.Empty()) with { Signature = "not-the-signature" };

        await harness.Sut.DispatchAsync(envelope, CancellationToken.None);

        harness.Mediator.Verify(m => m.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        var entry = Assert.Single(harness.Logger.Collector.GetSnapshot(), e => e.Level == LogLevel.Warning);
        Assert.Equal(LogEvents.CommandProcessing.CommandEnvelopeIntegrityWarning, entry.Id.Id);
        Assert.Contains("does not verify", entry.Message);
        Assert.DoesNotContain(harness.Logger.Collector.GetSnapshot(), e => e.Id.Id == LogEvents.CommandProcessing.CommandEnvelopeUnsignedWarning);
    }

    [Fact]
    public async Task DispatchAsync_OffMode_NoVerificationDespiteSignerRegistered()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Off, signer);
        var envelope = NewEnvelope(new PlainCommand("hello"), SessionContext.Empty()) with { Signature = "garbage" };

        await harness.Sut.DispatchAsync(envelope, CancellationToken.None);

        harness.Mediator.Verify(m => m.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class InMemoryEnvelopeSigner : IBusEnvelopeSigner
    {
        public string Sign(string payload) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sig:" + payload));

        public bool Verify(string payload, string? signature) =>
            !string.IsNullOrEmpty(signature) && signature == Sign(payload);
    }

    private static CommandEnvelope NewEnvelope<T>(T command, SessionContext session) where T : ICommand =>
        new(
            Guid.NewGuid(),
            JsonSerializer.Serialize(command),
            typeof(T).GetQualifiedTypeName(),
            JsonSerializer.Serialize(session));

    private sealed class Harness
    {
        public Mock<IMessageBus> MessageBus { get; } = new();
        public Mock<IMessagingIdentifier> MessagingIdentifier { get; } = new();
        public Mock<ResiliencePipelineProvider<string>> PipelineProvider { get; } = new();
        public Mock<ISessionContextProvider> SessionContextProvider { get; } = new();
        public Mock<IMediator> Mediator { get; } = new();
        public FakeLogger<MediatorCommandWorker> Logger { get; } = new();

        public MediatorCommandWorker Sut { get; }

        public Harness(BusEnvelopeIntegrityMode integrityMode = BusEnvelopeIntegrityMode.Off, IBusEnvelopeSigner? signer = null)
        {
            PipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);

            var services = new ServiceCollection();
            services.AddSingleton(SessionContextProvider.Object);
            services.AddSingleton(Mediator.Object);
            var serviceProvider = services.BuildServiceProvider();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

            var resolver = new TrustedTypeResolver();
            resolver.Register(typeof(PlainCommand));
            resolver.Register(typeof(AggregateCommand));

            var serializer = new Mock<ISecureJsonSerializer>();
            serializer.Setup(s => s.DeserializeAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .Returns<string, Type, Guid?, Guid?, CancellationToken>((json, type, _, _, _) =>
                    Task.FromResult(JsonSerializer.Deserialize(json, type)));

            Sut = new MediatorCommandWorker(
                Logger,
                MessageBus.Object,
                MessagingIdentifier.Object,
                scopeFactory,
                PipelineProvider.Object,
                resolver,
                serializer.Object,
                Options.Create(new BusEnvelopeJsonOptions()),
                Options.Create(new BusEnvelopeIntegrityOptions { Mode = integrityMode }),
                signer);
        }
    }
}
