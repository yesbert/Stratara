using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Mediator;
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
/// has to read the aggregate back from a payload that may be protected.
/// </summary>
internal sealed class IntentRecorder(ICommandIntentStore intents, ISecureJsonSerializer serializer, ILogger<IntentRecorder> logger)
{
    /// <summary>Records the intent and returns the payload its grain receives.</summary>
    public async Task<AggregateCommandEnvelope> RecordAsync<T>(Guid intentId, T command, SessionContext session, Guid? aggregateId, bool heavy, CancellationToken cancellationToken)
        where T : ICommand
    {
        var envelope = new CommandEnvelope(
            intentId,
            await serializer.SerializeAsync(command, session.TenantId, session.ActorUserId, cancellationToken),
            command.GetType().GetQualifiedTypeName(),
            JsonSerializer.Serialize(session),
            Heavy: heavy);

        await intents.RecordAsync(intentId, envelope, aggregateId, heavy, cancellationToken);
        ApplicationDiagnostics.Metrics.OrleansIntentRecorded.Add(1);
        logger.LogCommandRecorded(intentId);

        return new AggregateCommandEnvelope(envelope.CommandTypeName, envelope.CommandJson, envelope.SessionContextJson);
    }
}
