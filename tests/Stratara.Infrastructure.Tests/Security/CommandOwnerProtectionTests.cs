using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Infrastructure.Security;
using Stratara.Infrastructure.Security.Serialization;
using Stratara.Outbox.RabbitMQ.Mediator;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Testing;

namespace Stratara.Infrastructure.Tests.Security;

/// <summary>
/// <c>mediator-dispatch</c> → a command can name the owner of its protected fields, and
/// <c>outbox-and-messaging</c> → a deferred command carries the owner it names. Each test dispatches a
/// command through the bus dispatcher with the real serializer and key store on a host outside
/// development, puts the published envelope through JSON as the wire does, and hands it to the command
/// worker.
/// </summary>
public class CommandOwnerProtectionTests
{
    private const string Address = "https://example.test/a-page-somebody-visited";

    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid OtherTenant = Guid.CreateVersion7();
    private static readonly Guid Operator = Guid.CreateVersion7();

    public sealed record TrackPage(Guid Tenant, [property: EncryptData(DataSensitivityLevel.TenantScoped)] string? Address)
        : ICommand, INamesDataOwner
    {
        Guid INamesDataOwner.DataOwnerTenantId => Tenant;
    }

    public sealed record TrackPageWithoutAnOwner([property: EncryptData(DataSensitivityLevel.TenantScoped)] string? Address) : ICommand;

    private static SessionContext SessionWithoutATenant() => SessionContext.Empty();

    private static SessionContext SessionOfAnotherTenant() =>
        SessionContext.Empty() with { ActorTenantId = OtherTenant, ActorUserId = Operator, TenantId = OtherTenant };

    /// <summary>The finding, pinned: without a named owner, a session with no tenant cannot protect a tenant-scoped field.</summary>
    [Fact]
    public async Task ACommandNamingNoOwner_FromASessionWithoutATenant_IsRefusedOutsideDevelopment()
    {
        var fixture = new Fixture(SessionWithoutATenant());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Dispatcher.EnqueueCommandAsync(new TrackPageWithoutAnOwner(Address), TestContext.Current.CancellationToken));

        Assert.Contains(nameof(DataSensitivityLevel.TenantScoped), exception.Message);
        Assert.Empty(fixture.Published);
    }

    [Fact]
    public async Task ACommandNamingAnOwner_FromASessionWithoutATenant_ReachesItsHandlerWithItsProtectedField()
    {
        var session = SessionWithoutATenant();
        var fixture = new Fixture(session);

        await fixture.Dispatcher.EnqueueCommandAsync(new TrackPage(Owner, Address), TestContext.Current.CancellationToken);
        var envelope = fixture.TheEnvelopeOffTheWire();
        await fixture.Worker.DispatchAsync(envelope, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Address, envelope.CommandJson);
        Assert.Equal(Owner, envelope.DataOwnerTenantId);
        Assert.Equal(new TrackPage(Owner, Address), Assert.Single(fixture.Handled));
        Assert.Equal(session, Assert.Single(fixture.HandledUnder));
    }

    [Fact]
    public async Task ACommandNamingAnOwner_FromAnotherTenantsSession_IsWrittenForTheNamedTenantAlone()
    {
        var fixture = new Fixture(SessionOfAnotherTenant());

        await fixture.Dispatcher.EnqueueCommandAsync(new TrackPage(Owner, Address), TestContext.Current.CancellationToken);

        var scope = Assert.Single(await fixture.Keys.ListScopesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DataSensitivityLevel.TenantScoped, scope.Level);
        Assert.Equal(Owner.ToString(), scope.TenantId);
        Assert.True(string.IsNullOrEmpty(scope.UserId));
    }

    /// <summary>A command that names no owner keeps today's rule: the session's tenant and the acting user.</summary>
    [Fact]
    public async Task ACommandNamingNoOwner_IsWrittenForTheSessionsTenantAndActingUser()
    {
        var fixture = new Fixture(SessionOfAnotherTenant());

        await fixture.Dispatcher.EnqueueCommandAsync(new TrackPageWithoutAnOwner(Address), TestContext.Current.CancellationToken);
        var envelope = fixture.TheEnvelopeOffTheWire();
        await fixture.Worker.DispatchAsync(envelope, TestContext.Current.CancellationToken);

        var scope = Assert.Single(await fixture.Keys.ListScopesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(OtherTenant.ToString(), scope.TenantId);
        Assert.Equal(Operator.ToString(), scope.UserId);
        Assert.Null(envelope.DataOwnerTenantId);
        Assert.Equal(new TrackPageWithoutAnOwner(Address), Assert.Single(fixture.Handled));
    }

    [Fact]
    public async Task TheOwnersErasure_WhileTheCommandWaits_DeliversItWithTheFieldAbsent()
    {
        var fixture = new Fixture(SessionWithoutATenant());
        await fixture.Dispatcher.EnqueueCommandAsync(new TrackPage(Owner, Address), TestContext.Current.CancellationToken);
        var envelope = fixture.TheEnvelopeOffTheWire();

        await fixture.Eraser().EraseTenantAsync(Owner, TestContext.Current.CancellationToken);
        await fixture.Worker.DispatchAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Equal(new TrackPage(Owner, null), Assert.Single(fixture.Handled));
    }

    [Fact]
    public async Task TheErasureOfTheDispatchingSessionsTenant_DoesNotReachACommandNamingAnotherOwner()
    {
        var fixture = new Fixture(SessionOfAnotherTenant());
        await fixture.Dispatcher.EnqueueCommandAsync(new TrackPage(Owner, Address), TestContext.Current.CancellationToken);
        var envelope = fixture.TheEnvelopeOffTheWire();

        await fixture.Eraser().EraseTenantAsync(OtherTenant, TestContext.Current.CancellationToken);
        await fixture.Worker.DispatchAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Equal(new TrackPage(Owner, Address), Assert.Single(fixture.Handled));
    }

    /// <summary>
    /// What a host on a release before the owner does with such a command outside strict mode: it reads
    /// for the session, as it always has, and the read fails authentication. It does not come back with
    /// the field emptied, so the handler is not run on a command that only looks complete.
    /// </summary>
    [Fact]
    public async Task AHostThatReadsForTheSession_FailsACommandNamingAnotherOwner_RatherThanEmptyingItsField()
    {
        var session = SessionOfAnotherTenant();
        var fixture = new Fixture(session);
        await fixture.Dispatcher.EnqueueCommandAsync(new TrackPage(Owner, Address), TestContext.Current.CancellationToken);
        var envelope = fixture.TheEnvelopeOffTheWire();

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => fixture.Serializer.DeserializeAsync(envelope.CommandJson, typeof(TrackPage), session.TenantId, session.ActorUserId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InStrictMode_ACommandNamingAnOwner_VerifiesAndReachesItsHandler()
    {
        var fixture = new Fixture(SessionWithoutATenant(), BusEnvelopeIntegrityMode.Strict);

        await fixture.Dispatcher.EnqueueCommandAsync(new TrackPage(Owner, Address), TestContext.Current.CancellationToken);
        var envelope = fixture.TheEnvelopeOffTheWire();
        await fixture.Worker.DispatchAsync(envelope, TestContext.Current.CancellationToken);

        Assert.NotNull(envelope.Signature);
        Assert.Equal(new TrackPage(Owner, Address), Assert.Single(fixture.Handled));
    }

    [Fact]
    public async Task InStrictMode_ACommandWhoseOwnerWasAlteredOnTheWire_IsNotHandled()
    {
        var fixture = new Fixture(SessionWithoutATenant(), BusEnvelopeIntegrityMode.Strict);
        await fixture.Dispatcher.EnqueueCommandAsync(new TrackPage(Owner, Address), TestContext.Current.CancellationToken);
        var altered = fixture.TheEnvelopeOffTheWire() with { DataOwnerTenantId = OtherTenant };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Worker.DispatchAsync(altered, TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Handled);
    }

    private sealed class Fixture
    {
        private readonly InMemoryTenantMembershipStore _memberships = new();

        public InMemoryKeyStore Keys { get; } = new();
        public SecureJsonSerializer Serializer { get; }
        public CommandOutboxDispatcher Dispatcher { get; }
        public MediatorCommandWorker Worker { get; }
        public List<CommandEnvelope> Published { get; } = [];
        public List<object> Handled { get; } = [];
        public List<SessionContext> HandledUnder { get; } = [];

        public Fixture(SessionContext session, BusEnvelopeIntegrityMode mode = BusEnvelopeIntegrityMode.Off)
        {
            var encryption = new ServiceCollection().AddStrataraBlobEncryption().BuildServiceProvider()
                .GetRequiredService<IEncryptionFactory>();
            Serializer = new SecureJsonSerializer(Keys, encryption, NullLogger<SecureJsonSerializer>.Instance, new ProductionEnvironment());
            var signer = mode == BusEnvelopeIntegrityMode.Off ? null : new Signer();

            var pipelines = new Mock<ResiliencePipelineProvider<string>>();
            pipelines.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);
            var identifier = new Mock<IMessagingIdentifier>();
            identifier.SetupGet(i => i.CommandTopic).Returns("commands");
            identifier.Setup(i => i.GetCommandTopic(It.IsAny<Type>())).Returns("commands");
            var bus = new Mock<IMessageBus>();
            bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<CommandEnvelope>(), It.IsAny<CancellationToken>()))
                .Callback<string, CommandEnvelope, CancellationToken>((_, envelope, _) => Published.Add(envelope))
                .Returns(Task.CompletedTask);
            var dispatchingSession = new Mock<ISessionContextProvider>();
            dispatchingSession.SetupGet(s => s.Current).Returns(session);

            Dispatcher = new CommandOutboxDispatcher(
                NullLogger<CommandOutboxDispatcher>.Instance,
                Mock.Of<IWriteUnitOfWork>(),
                bus.Object,
                identifier.Object,
                dispatchingSession.Object,
                pipelines.Object,
                Mock.Of<IProjectionReplayState>(),
                Serializer,
                signer);

            var handlingSession = new Mock<ISessionContextProvider>();
            handlingSession.Setup(s => s.Set(It.IsAny<SessionContext>())).Callback<SessionContext>(HandledUnder.Add);
            var mediator = new Mock<IMediator>();
            mediator.Setup(m => m.HandleAsync(It.IsAny<TrackPage>(), It.IsAny<CancellationToken>()))
                .Callback<TrackPage, CancellationToken>((command, _) => Handled.Add(command))
                .Returns(Task.CompletedTask);
            mediator.Setup(m => m.HandleAsync(It.IsAny<TrackPageWithoutAnOwner>(), It.IsAny<CancellationToken>()))
                .Callback<TrackPageWithoutAnOwner, CancellationToken>((command, _) => Handled.Add(command))
                .Returns(Task.CompletedTask);
            var services = new ServiceCollection()
                .AddSingleton(handlingSession.Object)
                .AddSingleton(mediator.Object)
                .BuildServiceProvider();
            var resolver = new TrustedTypeResolver();
            resolver.Register(typeof(TrackPage));
            resolver.Register(typeof(TrackPageWithoutAnOwner));

            Worker = new MediatorCommandWorker(
                NullLogger<MediatorCommandWorker>.Instance,
                Mock.Of<IMessageBus>(),
                Mock.Of<IMessagingIdentifier>(),
                services.GetRequiredService<IServiceScopeFactory>(),
                pipelines.Object,
                resolver,
                Serializer,
                Options.Create(new BusEnvelopeJsonOptions()),
                Options.Create(new BusEnvelopeIntegrityOptions { Mode = mode }),
                signer);
        }

        public CommandEnvelope TheEnvelopeOffTheWire() =>
            JsonSerializer.Deserialize<CommandEnvelope>(JsonSerializer.Serialize(Assert.Single(Published)))!;

        public SubjectEraser Eraser() =>
            new(_memberships, new InMemoryApiKeyStore(_memberships), new InMemorySettingStore(), Keys);
    }

    private sealed class Signer : IBusEnvelopeSigner
    {
        public string Sign(string payload) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("key:" + payload)));

        public bool Verify(string payload, string? signature) => signature == Sign(payload);
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
