using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Security;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Diagnostics;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A recorded command is signed where the host has a signer, and verified under the host's integrity mode before it is
/// resumed: under strict mode an unsigned or tampered record is kept at once with the reason and no attempt, under
/// permissive mode it is resumed and the failure logged, and off verifies nothing (scenario <em>A record written before
/// the host signed is resumed</em>). A pass claims its batch in one call, and the port's default claims row by row.
/// </summary>
public sealed class IntentRecordIntegrityTests
{
    private static readonly SessionContext Session = new("corr", null, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);

    [Fact]
    public async Task A_recorder_with_a_signer_records_the_signature_over_the_canonical_form()
    {
        var stored = await RecordAsync(new FakeSigner());

        Assert.Equal(FakeSigner.SignatureOf(BusEnvelopeCanonical.Of(stored with { Signature = null })), stored.Signature);
    }

    [Fact]
    public async Task A_recorder_without_a_signer_records_no_signature()
    {
        var stored = await RecordAsync(signer: null);

        Assert.Null(stored.Signature);
    }

    [Theory]
    [InlineData(BusEnvelopeIntegrityMode.Off, "signed", true, 0)]
    [InlineData(BusEnvelopeIntegrityMode.Off, "unsigned", true, 0)]
    [InlineData(BusEnvelopeIntegrityMode.Off, "tampered", true, 0)]
    [InlineData(BusEnvelopeIntegrityMode.Permissive, "signed", true, 0)]
    [InlineData(BusEnvelopeIntegrityMode.Permissive, "unsigned", true, LogEvents.Orleans.IntentUnsignedResumed)]
    [InlineData(BusEnvelopeIntegrityMode.Permissive, "tampered", true, LogEvents.Orleans.IntentIntegrityResumed)]
    [InlineData(BusEnvelopeIntegrityMode.Strict, "signed", true, 0)]
    [InlineData(BusEnvelopeIntegrityMode.Strict, "unsigned", false, LogEvents.Orleans.IntentUnsignedKept)]
    [InlineData(BusEnvelopeIntegrityMode.Strict, "tampered", false, LogEvents.Orleans.IntentIntegrityKept)]
    public async Task A_due_record_is_verified_under_the_mode_before_it_is_resumed(BusEnvelopeIntegrityMode mode, string record, bool resumed, int eventId)
    {
        var signer = new FakeSigner();
        var envelope = new CommandEnvelope(Guid.NewGuid(), "{}", "Probe", "{\"TenantId\":\"a\"}");
        envelope = record switch
        {
            "signed" => envelope with { Signature = FakeSigner.SignatureOf(BusEnvelopeCanonical.Of(envelope)) },
            "tampered" => envelope with { Signature = FakeSigner.SignatureOf(BusEnvelopeCanonical.Of(envelope)), SessionContextJson = "{\"TenantId\":\"b\"}" },
            _ => envelope,
        };
        var intent = new RecordedIntent(envelope.Id, envelope, Guid.NewGuid(), Heavy: false, AttemptCount: 0, LastHandedOverAt: null, LastFailure: null);
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.GetDueAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([intent]);
        intents.Setup(s => s.ClaimAsync(It.IsAny<IReadOnlyList<RecordedIntent>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<RecordedIntent> due, DateTimeOffset _, CancellationToken _) => [.. due.Select(d => d.Id)]);
        var grains = new RecordedCommandDrainTests.Grains();
        var logger = new RecordingLogger();

        var pass = await Resumer(intents.Object, grains, logger, signer, mode).ResumeDueAsync(10, CancellationToken.None);

        Assert.Equal(resumed ? 1 : 0, pass.Resumed);
        Assert.Equal(resumed ? [intent.Id] : [], grains.Accepted);
        if (resumed)
        {
            intents.Verify(s => s.KeepAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            intents.Verify(s => s.KeepAsync(intent.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
            intents.Verify(s => s.RecordFailureAsync(intent.Id, It.Is<string>(reason => reason.Contains(record == "unsigned" ? "no signature" : "does not verify")), It.IsAny<CancellationToken>()), Times.Once);
            intents.Verify(s => s.ClaimAsync(It.IsAny<IReadOnlyList<RecordedIntent>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        var integrityEvents = logger.Entries.Select(e => e.EventId.Id).Where(id => id is >= LogEvents.Orleans.IntentUnsignedResumed and <= LogEvents.Orleans.IntentIntegrityKept).ToList();
        Assert.Equal(eventId == 0 ? [] : [eventId], integrityEvents);
    }

    [Fact]
    public async Task A_pass_claims_its_batch_in_one_call_and_hands_over_only_what_it_claimed_in_read_order()
    {
        var due = Enumerable.Range(0, 3)
            .Select(_ => new RecordedIntent(Guid.NewGuid(), new CommandEnvelope(Guid.NewGuid(), "{}", "Probe", "{}"), Guid.NewGuid(), false, 0, null, null))
            .ToList();
        var intents = new Mock<ICommandIntentStore>(MockBehavior.Strict);
        intents.Setup(s => s.GetDueAsync(It.IsAny<DateTimeOffset>(), 3, It.IsAny<CancellationToken>())).ReturnsAsync(due);
        intents.Setup(s => s.ClaimAsync(due, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([due[2].Id, due[0].Id]);
        var grains = new RecordedCommandDrainTests.Grains();

        var pass = await Resumer(intents.Object, grains, new RecordingLogger(), signer: null, BusEnvelopeIntegrityMode.Off).ResumeDueAsync(3, CancellationToken.None);

        Assert.Equal(new ResumePass(2, Full: true), pass);
        Assert.Equal([due[0].Id, due[2].Id], grains.Accepted);
    }

    [Fact]
    public async Task The_ports_default_claim_goes_through_the_per_row_claim_and_returns_what_it_claimed()
    {
        var store = new PerRowStore();
        var due = Enumerable.Range(0, 3)
            .Select(i => new RecordedIntent(Guid.NewGuid(), new CommandEnvelope(Guid.NewGuid(), "{}", "Probe", "{}"), null, false, 0, i == 1 ? DateTimeOffset.UnixEpoch : null, null))
            .ToList();
        store.Refuse = due[1].Id;

        var claimed = await ((ICommandIntentStore)store).ClaimAsync(due, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal([due[0].Id, due[2].Id], claimed);
        Assert.Equal(due.Select(d => (d.Id, d.LastHandedOverAt)), store.Calls);
    }

    private static IntentResumer Resumer(ICommandIntentStore intents, RecordedCommandDrainTests.Grains grains, RecordingLogger logger, IBusEnvelopeSigner? signer, BusEnvelopeIntegrityMode mode) => new(
        intents,
        new IntentHandOver(grains.Factory, Options.Create(new HeavyWorkOptions())),
        new AggregateSendLane(),
        Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.Zero }),
        Options.Create(new MessageRetryOptions()),
        new FakeTimeProvider(DateTimeOffset.UtcNow),
        new TypedLogger<IntentResumer>(logger),
        signer,
        Options.Create(new BusEnvelopeIntegrityOptions { Mode = mode }));

    private static async Task<CommandEnvelope> RecordAsync(IBusEnvelopeSigner? signer)
    {
        CommandEnvelope? stored = null;
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.RecordAsync(It.IsAny<Guid>(), It.IsAny<CommandEnvelope>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, CommandEnvelope envelope, Guid? _, bool _, CancellationToken _) => stored = envelope)
            .Returns(Task.CompletedTask);
        var serializer = new Mock<ISecureJsonSerializer>();
        serializer.Setup(s => s.SerializeAsync(It.IsAny<It.IsAnyType>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("{\"body\":1}");
        var recorder = new IntentRecorder(intents.Object, serializer.Object, new TypedLogger<IntentRecorder>(new RecordingLogger()), signer);

        await recorder.RecordAsync(Guid.NewGuid(), new SignedProbe(), Session, Guid.NewGuid(), heavy: true, CancellationToken.None);

        return stored ?? throw new InvalidOperationException("nothing was recorded");
    }

    private sealed record SignedProbe : ICommand;

    private sealed class FakeSigner : IBusEnvelopeSigner
    {
        public static string SignatureOf(string canonical) => $"sig:{canonical.GetHashCode(StringComparison.Ordinal)}";

        public string Sign(string canonical) => SignatureOf(canonical);

        public bool Verify(string canonical, string? signature) => SignatureOf(canonical) == signature;
    }

    private sealed class PerRowStore : ICommandIntentStore
    {
        public Guid Refuse { get; set; }

        public List<(Guid, DateTimeOffset?)> Calls { get; } = [];

        public Task<bool> TryClaimAsync(Guid intentId, DateTimeOffset? expectedLastHandedOverAt, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls.Add((intentId, expectedLastHandedOverAt));
            return Task.FromResult(intentId != Refuse);
        }

        public Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RecordedIntent>> GetDueAsync(DateTimeOffset handedOverBefore, int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RenewAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordFailureAsync(Guid intentId, string failure, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task KeepAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

/// <summary>An <see cref="ILogger{T}"/> over a <see cref="RecordingLogger"/>.</summary>
internal sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        inner.Log(logLevel, eventId, state, exception, formatter);
}
