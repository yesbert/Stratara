using System.Text.Json;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Shared.Reflections;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Records a command as a durable intent before it is handed to its grain: serialised under the
/// session that issued it, and written in a transaction of its own, so the record is committed
/// whatever the caller's unit of work does next.
/// </summary>
internal sealed class IntentRecorder(IWriteUnitOfWork unitOfWork, ISecureJsonSerializer serializer)
{
    /// <summary>Writes the intent and returns the payload its grain receives.</summary>
    public async Task<AggregateCommandEnvelope> RecordAsync<T>(Guid intentId, T command, SessionContext session, bool heavy, CancellationToken cancellationToken)
        where T : ICommand
    {
        var envelope = new CommandEnvelope(
            intentId,
            await serializer.SerializeAsync(command, session.TenantId, session.ActorUserId, cancellationToken),
            command.GetType().GetQualifiedTypeName(),
            JsonSerializer.Serialize(session),
            Heavy: heavy);

        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        await unitOfWork.CreateOutboxRepository(transaction).AddAsync(envelope, cancellationToken);
        await transaction.SaveChangesAsync(cancellationToken);

        return new AggregateCommandEnvelope(envelope.CommandTypeName, envelope.CommandJson, envelope.SessionContextJson);
    }
}
