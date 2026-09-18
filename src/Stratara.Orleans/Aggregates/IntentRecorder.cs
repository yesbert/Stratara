using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Security;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Diagnostics;
using Stratara.Orleans.Diagnostics;
using Stratara.Shared.Reflections;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Records a command as a durable intent before it is handed to its grain: serialised under the
/// session that issued it and kept by the intent store with the aggregate it names, so a resume never
/// has to read the aggregate back from a payload that may be protected. Where the host has a bus-envelope signer, the
/// record is signed over the canonical form a bus command is signed over, so the drain can verify it before it resumes.
/// </summary>
internal sealed class IntentRecorder(ICommandIntentStore intents, ISecureJsonSerializer serializer, ILogger<IntentRecorder> logger, IBusEnvelopeSigner? signer = null)
{
    /// <summary>Records the intent at the time its dispatch took, and returns the payload its grain receives.</summary>
    public async Task<AggregateCommandEnvelope> RecordAsync<T>(IntentDispatch dispatch, T command, CancellationToken cancellationToken)
        where T : ICommand
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var session = dispatch.Session;
        var envelope = new CommandEnvelope(
            dispatch.IntentId,
            await serializer.SerializeAsync(command, session.TenantId, session.ActorUserId, cancellationToken),
            command.GetType().GetQualifiedTypeName(),
            JsonSerializer.Serialize(session),
            Heavy: dispatch.Heavy);
        if (signer is not null)
        {
            envelope = envelope with { Signature = signer.Sign(BusEnvelopeCanonical.Of(envelope)) };
        }

        await intents.RecordAsync(dispatch.IntentId, envelope, dispatch.AggregateId, dispatch.Heavy, dispatch.RecordedAt, cancellationToken);
        ApplicationDiagnostics.Metrics.OrleansIntentRecorded.Add(1);
        logger.LogCommandRecorded(dispatch.IntentId);

        return new AggregateCommandEnvelope(envelope.CommandTypeName, envelope.CommandJson, envelope.SessionContextJson);
    }
}

/// <summary>What a dispatch records beside its command: its identity, its session, where it runs, and the time that orders it.</summary>
/// <param name="IntentId">The identity of the recorded command.</param>
/// <param name="Session">The session it was dispatched under.</param>
/// <param name="AggregateId">The aggregate it names, or <see langword="null"/>.</param>
/// <param name="Heavy">Whether it declared itself long-running.</param>
/// <param name="RecordedAt">When its dispatch began; orders it among the commands due with it.</param>
internal sealed record IntentDispatch(Guid IntentId, SessionContext Session, Guid? AggregateId, bool Heavy, DateTimeOffset RecordedAt);
