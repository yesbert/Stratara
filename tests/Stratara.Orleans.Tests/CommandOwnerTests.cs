using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Orleans.Aggregates;
using Stratara.Shared.Reflections;
using IRequest = Stratara.Abstractions.Mediator.IRequest;

namespace Stratara.Orleans.Tests;

/// <summary>
/// <c>orleans-execution</c> → a command's named owner holds on the execution model's path: its record and
/// what its activation receives are written for the named owner, the activation reads its protected
/// fields for that owner, and a command that names none is recorded, handed over and read as before.
/// </summary>
public sealed class CommandOwnerTests
{
    private static readonly Guid OwnerTenant = Guid.NewGuid();
    private static readonly Guid OwnerUser = Guid.NewGuid();

    private static readonly SessionContext Session = new("corr", null, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    public sealed record Plain(Guid AggregateId, string Text) : ICommand, IAggregateScopedCommand;

    public sealed record Owned(Guid AggregateId, Guid Tenant, Guid? User, string Text) : ICommand, IAggregateScopedCommand, INamesDataOwner
    {
        Guid INamesDataOwner.DataOwnerTenantId => Tenant;

        Guid? INamesDataOwner.DataOwnerUserId => User;
    }

    [Fact]
    public async Task A_recorded_command_naming_an_owner_is_recorded_and_handed_over_for_that_owner()
    {
        var serializer = new RecordingSerializer();
        var (recorder, stored) = Recorder(serializer);

        var payload = await recorder.RecordAsync(ADispatch(), new Owned(Guid.NewGuid(), OwnerTenant, OwnerUser, "x"), CancellationToken.None);

        var envelope = Assert.Single(stored);
        Assert.Equal((OwnerTenant, (Guid?)OwnerUser), Assert.Single(serializer.WrittenFor));
        Assert.Equal(OwnerTenant, envelope.DataOwnerTenantId);
        Assert.Equal(OwnerUser, envelope.DataOwnerUserId);
        Assert.Equal(OwnerTenant, payload.DataOwnerTenantId);
        Assert.Equal(OwnerUser, payload.DataOwnerUserId);
        Assert.Equal(Session, JsonSerializer.Deserialize<SessionContext>(payload.SessionContextJson));
    }

    [Fact]
    public async Task A_recorded_command_naming_no_owner_is_recorded_and_handed_over_as_before()
    {
        var serializer = new RecordingSerializer();
        var (recorder, stored) = Recorder(serializer);

        var payload = await recorder.RecordAsync(ADispatch(), new Plain(Guid.NewGuid(), "x"), CancellationToken.None);

        var envelope = Assert.Single(stored);
        Assert.Equal((Session.TenantId, (Guid?)Session.ActorUserId), Assert.Single(serializer.WrittenFor));
        Assert.Null(envelope.DataOwnerTenantId);
        Assert.DoesNotContain("DataOwner", JsonSerializer.Serialize(envelope));
        Assert.Null(payload.DataOwnerTenantId);
        Assert.Null(payload.DataOwnerUserId);
    }

    [Fact]
    public async Task A_recorded_command_naming_an_owner_is_signed_over_its_owner()
    {
        var (recorder, stored) = Recorder(new RecordingSerializer(), new Signer());

        await recorder.RecordAsync(ADispatch(), new Owned(Guid.NewGuid(), OwnerTenant, null, "x"), CancellationToken.None);

        var envelope = Assert.Single(stored);
        Assert.Equal(Signer.SignatureOf(BusEnvelopeCanonical.Of(envelope)), envelope.Signature);
        Assert.NotEqual(Signer.SignatureOf(BusEnvelopeCanonical.Of(envelope with { DataOwnerTenantId = null })), envelope.Signature);
    }

    [Fact]
    public async Task A_command_naming_an_empty_tenant_is_refused_and_nothing_is_recorded()
    {
        var serializer = new RecordingSerializer();
        var (recorder, stored) = Recorder(serializer);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => recorder.RecordAsync(ADispatch(), new Owned(Guid.NewGuid(), Guid.Empty, null, "x"), CancellationToken.None));

        Assert.Contains(nameof(Owned), exception.Message);
        Assert.Empty(stored);
        Assert.Empty(serializer.WrittenFor);
    }

    [Fact]
    public async Task A_resumed_command_is_handed_over_with_the_owner_its_record_names()
    {
        var envelope = new CommandEnvelope(Guid.NewGuid(), "{}", "Probe", "{}") { DataOwnerTenantId = OwnerTenant, DataOwnerUserId = OwnerUser };
        var grain = new CapturingAggregate();

        await Resumer(envelope, grain).ResumeDueAsync(10, CancellationToken.None);

        var handedOver = Assert.Single(grain.Accepted);
        Assert.Equal(OwnerTenant, handedOver.DataOwnerTenantId);
        Assert.Equal(OwnerUser, handedOver.DataOwnerUserId);
        Assert.NotNull(handedOver.ClaimedAt);
    }

    [Fact]
    public async Task A_resumed_command_naming_no_owner_is_handed_over_without_one()
    {
        var grain = new CapturingAggregate();

        await Resumer(new CommandEnvelope(Guid.NewGuid(), "{}", "Probe", "{}"), grain).ResumeDueAsync(10, CancellationToken.None);

        var handedOver = Assert.Single(grain.Accepted);
        Assert.Null(handedOver.DataOwnerTenantId);
        Assert.Null(handedOver.DataOwnerUserId);
    }

    [Fact]
    public async Task A_forwarded_command_naming_an_owner_is_written_for_that_owner()
    {
        var serializer = new RecordingSerializer();
        var command = new Owned(Guid.NewGuid(), OwnerTenant, OwnerUser, "x");

        var forwarded = await ForwardAsync(command, serializer);

        Assert.Equal((OwnerTenant, (Guid?)OwnerUser), Assert.Single(serializer.WrittenFor));
        Assert.Equal(OwnerTenant, forwarded.DataOwnerTenantId);
        Assert.Equal(OwnerUser, forwarded.DataOwnerUserId);
    }

    [Fact]
    public async Task A_forwarded_command_naming_no_owner_is_written_as_before()
    {
        var serializer = new RecordingSerializer();

        var forwarded = await ForwardAsync(new Plain(Guid.NewGuid(), "x"), serializer);

        Assert.Equal((Session.TenantId, (Guid?)Session.ActorUserId), Assert.Single(serializer.WrittenFor));
        Assert.Null(forwarded.DataOwnerTenantId);
    }

    [Fact]
    public async Task A_forwarded_command_naming_an_empty_tenant_is_refused_before_it_reaches_its_activation()
    {
        var serializer = new RecordingSerializer();
        var command = new Owned(Guid.NewGuid(), Guid.Empty, null, "x");
        var grain = new CapturingAggregate();
        var factory = new Mock<IGrainFactory>();
        factory.Setup(f => f.GetGrain<IAggregateGrain>(command.AggregateId, null)).Returns(grain);
        var sessions = new Mock<ISessionContextProvider>();
        sessions.SetupGet(s => s.Current).Returns(Session);
        var behaviour = new AggregateGrainBehavior<Owned>(factory.Object, sessions.Object, serializer, new AggregateSendLane());
        var reachedHandler = false;

        await Assert.ThrowsAsync<ArgumentException>(() => behaviour.HandleAsync(
            command,
            () =>
            {
                reachedHandler = true;
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.False(reachedHandler);
        Assert.Empty(grain.Executed);
        Assert.Empty(serializer.WrittenFor);
    }

    [Fact]
    public async Task An_activation_reads_a_command_for_the_owner_its_envelope_names_and_runs_under_the_carried_session()
    {
        var serializer = new RecordingSerializer();
        var command = new Owned(Guid.NewGuid(), OwnerTenant, OwnerUser, "x");
        var envelope = new AggregateCommandEnvelope(
            typeof(Owned).GetQualifiedTypeName(), JsonSerializer.Serialize(command), JsonSerializer.Serialize(Session),
            DataOwnerTenantId: OwnerTenant, DataOwnerUserId: OwnerUser);

        var (handled, ranUnder) = await RunAsync<Owned>(envelope, serializer);

        Assert.Equal((OwnerTenant, (Guid?)OwnerUser), Assert.Single(serializer.ReadFor));
        Assert.Equal(command, handled);
        Assert.Equal(Session, ranUnder);
    }

    [Fact]
    public async Task An_activation_reads_a_command_naming_no_owner_for_the_session()
    {
        var serializer = new RecordingSerializer();
        var command = new Plain(Guid.NewGuid(), "x");
        var envelope = new AggregateCommandEnvelope(
            typeof(Plain).GetQualifiedTypeName(), JsonSerializer.Serialize(command), JsonSerializer.Serialize(Session));

        var (handled, _) = await RunAsync<Plain>(envelope, serializer);

        Assert.Equal((Session.TenantId, (Guid?)Session.ActorUserId), Assert.Single(serializer.ReadFor));
        Assert.Equal(command, handled);
    }

    private static IntentDispatch ADispatch() => new(Guid.NewGuid(), Session, Guid.NewGuid(), Heavy: false, DateTimeOffset.UtcNow);

    private static (IntentRecorder Recorder, List<CommandEnvelope> Stored) Recorder(ISecureJsonSerializer serializer, IBusEnvelopeSigner? signer = null)
    {
        var stored = new List<CommandEnvelope>();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.RecordAsync(It.IsAny<Guid>(), It.IsAny<CommandEnvelope>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, CommandEnvelope envelope, Guid? _, bool _, DateTimeOffset _, CancellationToken _) => stored.Add(envelope))
            .Returns(Task.CompletedTask);

        return (new IntentRecorder(intents.Object, serializer, new TypedLogger<IntentRecorder>(new RecordingLogger()), signer), stored);
    }

    private static IntentResumer Resumer(CommandEnvelope envelope, CapturingAggregate grain)
    {
        var intent = new RecordedIntent(envelope.Id, envelope, Guid.NewGuid(), Heavy: false, AttemptCount: 0, LastHandedOverAt: null, LastFailure: null);
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.GetDueAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([intent]);
        intents.Setup(s => s.ClaimAsync(It.IsAny<IReadOnlyList<RecordedIntent>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<RecordedIntent> due, DateTimeOffset _, CancellationToken _) => [.. due.Select(d => d.Id)]);
        var factory = new Mock<IGrainFactory>();
        factory.Setup(f => f.GetGrain<IAggregateGrain>(It.IsAny<Guid>(), null)).Returns(grain);

        return new IntentResumer(
            intents.Object,
            new IntentHandOver(factory.Object, Options.Create(new HeavyWorkOptions())),
            new AggregateSendLane(),
            Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.Zero }),
            Options.Create(new MessageRetryOptions()),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            new TypedLogger<IntentResumer>(new RecordingLogger()),
            signer: null,
            Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Off }));
    }

    private static async Task<AggregateCommandEnvelope> ForwardAsync<TCommand>(TCommand command, ISecureJsonSerializer serializer)
        where TCommand : IRequest, IAggregateScopedCommand
    {
        var grain = new CapturingAggregate();
        var factory = new Mock<IGrainFactory>();
        factory.Setup(f => f.GetGrain<IAggregateGrain>(command.AggregateId, null)).Returns(grain);
        var sessions = new Mock<ISessionContextProvider>();
        sessions.SetupGet(s => s.Current).Returns(Session);
        var behaviour = new AggregateGrainBehavior<TCommand>(factory.Object, sessions.Object, serializer, new AggregateSendLane());

        await behaviour.HandleAsync(command, () => Task.CompletedTask, CancellationToken.None);

        return Assert.Single(grain.Executed);
    }

    private static async Task<(TCommand? Handled, SessionContext? RanUnder)> RunAsync<TCommand>(AggregateCommandEnvelope envelope, ISecureJsonSerializer serializer)
        where TCommand : class, IRequest
    {
        TCommand? handled = null;
        SessionContext? ranUnder = null;
        var handler = new Mock<ICommandHandler<TCommand>>();
        handler.Setup(h => h.HandleAsync(It.IsAny<TCommand>(), It.IsAny<CancellationToken>()))
            .Callback<TCommand, CancellationToken>((command, _) => handled = command)
            .Returns(Task.CompletedTask);
        var sessions = new Mock<ISessionContextProvider>();
        sessions.Setup(s => s.Set(It.IsAny<SessionContext>())).Callback<SessionContext>(session => ranUnder = session);
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(TCommand));
        await using var services = new ServiceCollection()
            .AddSingleton(sessions.Object)
            .AddSingleton<ITrustedTypeResolver>(resolver)
            .AddSingleton(serializer)
            .AddSingleton(handler.Object)
            .BuildServiceProvider();

        await CommandExecution.RunAsync(services.GetRequiredService<IServiceScopeFactory>(), envelope, intentId: null, CancellationToken.None);

        return (handled, ranUnder);
    }

    private sealed class CapturingAggregate : IAggregateGrain
    {
        public List<AggregateCommandEnvelope> Executed { get; } = [];

        public List<AggregateCommandEnvelope> Accepted { get; } = [];

        public Task ExecuteAsync(AggregateCommandEnvelope envelope)
        {
            Executed.Add(envelope);
            return Task.CompletedTask;
        }

        public Task AcceptIntentAsync(Guid intentId, AggregateCommandEnvelope envelope)
        {
            Accepted.Add(envelope);
            return Task.CompletedTask;
        }

        public Task RunAcceptedAsync() => Task.CompletedTask;
    }

    private sealed class Signer : IBusEnvelopeSigner
    {
        public static string SignatureOf(string canonical) => $"sig:{canonical}";

        public string Sign(string canonical) => SignatureOf(canonical);

        public bool Verify(string canonical, string? signature) => SignatureOf(canonical) == signature;
    }

    private sealed class RecordingSerializer : ISecureJsonSerializer
    {
        public List<(Guid? TenantId, Guid? UserId)> WrittenFor { get; } = [];

        public List<(Guid? TenantId, Guid? UserId)> ReadFor { get; } = [];

        public Task<string> SerializeAsync<T>(T obj, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default) =>
            SerializeAsync((object)obj!, tenantId, userId, cancellationToken);

        public Task<string> SerializeAsync(object obj, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default)
        {
            WrittenFor.Add((tenantId, userId));
            return Task.FromResult(JsonSerializer.Serialize(obj, obj.GetType()));
        }

        public Task<T?> DeserializeAsync<T>(string json, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default)
        {
            ReadFor.Add((tenantId, userId));
            return Task.FromResult(JsonSerializer.Deserialize<T>(json));
        }

        public Task<object?> DeserializeAsync(string json, Type returnType, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default)
        {
            ReadFor.Add((tenantId, userId));
            return Task.FromResult(JsonSerializer.Deserialize(json, returnType));
        }
    }
}
