using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Shared.Partitioning;
using Stratara.Shared.Reflections;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Entities;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Repositories;

/// <summary>
/// EF Core-backed <see cref="ICommandAuditRepository"/> that writes a row into
/// <c>command_log_entry</c> for every dispatched command, capturing actor and subject identity,
/// the encrypted JSON payload, and the correlation/causation ids needed to reconstruct the
/// command chain.
/// </summary>
/// <remarks>
/// The audit row is tagged by Subject (data owner) via <c>IMultiTenant.TenantId</c>, so tenant
/// query filters return the rows belonging to that tenant's data regardless of who actually
/// issued the command. The actor identity is preserved separately for "who did this" queries.
/// Updates the session context's <c>CausationId</c> with the freshly minted command id so any
/// subsequent appended events inherit the causation link.
/// </remarks>
/// <param name="context">The write-store DbContext that hosts the command-log table.</param>
/// <param name="sessionContextProvider">Provides the ambient session context used for correlation/actor data.</param>
/// <param name="serializer">Secure JSON serializer used to encrypt the command payload with tenant-aware AAD.</param>
internal sealed class CommandAuditRepository(IWriteDbContext context, ISessionContextProvider sessionContextProvider, ISecureJsonSerializer serializer)
    : ICommandAuditRepository
{
    /// <inheritdoc/>
    public async Task<Guid> AddAsync(ICommandBase command, CancellationToken cancellationToken)
    {
        var sessionContext = sessionContextProvider.Current;
        if (sessionContext is null)
        {
            throw new InvalidOperationException("Session context is null");
        }

        var commandId = Guid.CreateVersion7();
        var correlationId = sessionContext.CorrelationId;
        var causationId = commandId.ToString("N");
        sessionContextProvider.Set(sessionContext with { CausationId = causationId });

        var logEntry = new CommandLogEntry
        {
            Id = commandId,
            BucketId = BucketCalculator.GetBucketId(commandId),
            CausationId = causationId,
            CorrelationId = correlationId,
            CommandJson = await serializer.SerializeAsync<object>(command, sessionContext.TenantId, sessionContext.UserId, cancellationToken),
            CommandTypeName = command.GetType().GetQualifiedTypeName(),
            Timestamp = DateTime.UtcNow,
            ActorTenantId = sessionContext.ActorTenantId,
            ActorUserId = sessionContext.ActorUserId,
            TenantId = sessionContext.TenantId,
            UserId = sessionContext.UserId
        };

        await context.Set<CommandLogEntry>().AddAsync(logEntry, cancellationToken);
        return commandId;
    }
}
