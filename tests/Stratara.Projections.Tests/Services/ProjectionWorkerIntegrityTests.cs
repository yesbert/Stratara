using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;

namespace Stratara.Projections.Tests.Services;

public class ProjectionWorkerIntegrityTests
{
    [Fact]
    public async Task HandleEventBundleAsync_OffMode_DispatchesWithoutVerification()
    {
        var harness = new Harness(BusEnvelopeIntegrityMode.Off, signer: new InMemoryEnvelopeSigner());
        var bundle = NewBundle() with { Signature = "garbage" };

        await harness.Sut.HandleEventBundleAsync(bundle, CancellationToken.None);

        harness.ProjectionManager.Verify(p => p.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleEventBundleAsync_StrictMode_ValidSignature_Dispatches()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Strict, signer);
        var unsigned = NewBundle();
        var signed = unsigned with { Signature = signer.Sign(BusEnvelopeCanonical.Of(unsigned)) };

        await harness.Sut.HandleEventBundleAsync(signed, CancellationToken.None);

        harness.ProjectionManager.Verify(p => p.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleEventBundleAsync_StrictMode_MissingSignature_Throws()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Strict, signer);
        var bundle = NewBundle();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.HandleEventBundleAsync(bundle, CancellationToken.None));

        Assert.Contains("integrity verification", ex.Message);
        harness.ProjectionManager.Verify(p => p.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleEventBundleAsync_StrictMode_TamperedSessionContext_Throws()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Strict, signer);
        var unsigned = NewBundle();
        var signed = unsigned with { Signature = signer.Sign(BusEnvelopeCanonical.Of(unsigned)) };
        var tampered = signed with
        {
            SessionContextJson = JsonSerializer.Serialize(SessionContext.Empty() with { TenantId = Guid.NewGuid() }),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.HandleEventBundleAsync(tampered, CancellationToken.None));

        harness.ProjectionManager.Verify(p => p.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleEventBundleAsync_PermissiveMode_MissingSignature_DispatchesAnyway()
    {
        var signer = new InMemoryEnvelopeSigner();
        var harness = new Harness(BusEnvelopeIntegrityMode.Permissive, signer);
        var bundle = NewBundle();

        await harness.Sut.HandleEventBundleAsync(bundle, CancellationToken.None);

        harness.ProjectionManager.Verify(p => p.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleEventBundleAsync_SessionContextJsonExceedsSizeLimit_ThrowsJsonException()
    {
        var harness = new Harness(BusEnvelopeIntegrityMode.Off, maxBodyBytes: 64);
        var bundle = NewBundle() with { SessionContextJson = new string('A', 256) };

        await Assert.ThrowsAsync<JsonException>(
            () => harness.Sut.HandleEventBundleAsync(bundle, CancellationToken.None));

        harness.ProjectionManager.Verify(p => p.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static EventBundle NewBundle()
    {
        var events = new[]
        {
            new EventMessage(
                Id: Guid.CreateVersion7(),
                Version: 1,
                DataJson: "{}",
                StreamId: Guid.NewGuid(),
                EventTypeName: "TestEvent",
                AggregateTypeName: "TestAggregate",
                ActorTenantId: Guid.Empty,
                ActorUserId: Guid.Empty,
                TenantId: Guid.Empty,
                UserId: null),
        };
        return new EventBundle(events, JsonSerializer.Serialize(SessionContext.Empty()));
    }

    private sealed class InMemoryEnvelopeSigner : IBusEnvelopeSigner
    {
        public string Sign(string payload) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sig:" + payload));

        public bool Verify(string payload, string? signature) =>
            !string.IsNullOrEmpty(signature) && signature == Sign(payload);
    }

    private sealed class Harness
    {
        public Mock<IMessageBus> MessageBus { get; } = new();
        public Mock<IMessagingIdentifier> MessagingIdentifier { get; } = new();
        public Mock<IEventMapperFactory> EventMapperFactory { get; } = new();
        public Mock<ResiliencePipelineProvider<string>> PipelineProvider { get; } = new();
        public Mock<ISessionContextProvider> SessionContextProvider { get; } = new();
        public Mock<IProjectionManager> ProjectionManager { get; } = new();

        public ProjectionWorker Sut { get; }

        public Harness(BusEnvelopeIntegrityMode integrityMode, IBusEnvelopeSigner? signer = null, int maxBodyBytes = 1_048_576)
        {
            PipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);
            EventMapperFactory.Setup(m => m.MapToEventsAsync(It.IsAny<IReadOnlyList<EventMessage>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<IEvent>());

            var services = new ServiceCollection();
            services.AddSingleton(SessionContextProvider.Object);
            services.AddSingleton(ProjectionManager.Object);
            var serviceProvider = services.BuildServiceProvider();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

            Sut = new ProjectionWorker(
                NullLogger<ProjectionWorker>.Instance,
                MessageBus.Object,
                MessagingIdentifier.Object,
                scopeFactory,
                EventMapperFactory.Object,
                PipelineProvider.Object,
                Options.Create(new BusEnvelopeJsonOptions { MaxBodyBytes = maxBodyBytes }),
                Options.Create(new BusEnvelopeIntegrityOptions { Mode = integrityMode }),
                signer);
        }
    }
}
