using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Shared.Partitioning;
using Stratara.Shared.Reflections;
using Stratara.Shared.Security;
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
/// The data owner is the one <see cref="CommandDataOwner.ForRecord"/> resolves: the owner the command
/// names through <see cref="INamesDataOwner"/>, or the session's data-owner tenant and user. The
/// payload is encrypted for that same owner, so a row can be read back from its own columns.
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
    /// <exception cref="SessionRequiredException">No session context is set.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> implements <see cref="INamesDataOwner"/> and names an empty tenant.</exception>
    public async Task<Guid> AddAsync(ICommandBase command, CancellationToken cancellationToken)
    {
        var sessionContext = sessionContextProvider.Current;
        if (sessionContext is null)
        {
            throw new SessionRequiredException("Session context is null");
        }

        var owner = CommandDataOwner.ForRecord(command, sessionContext);
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
            CommandJson = await serializer.SerializeAsync<object>(command, owner.TenantId, owner.UserId, cancellationToken),
            CommandTypeName = command.GetType().GetQualifiedTypeName(),
            Timestamp = DateTime.UtcNow,
            ActorTenantId = sessionContext.ActorTenantId,
            ActorUserId = sessionContext.ActorUserId,
            TenantId = owner.TenantId,
            UserId = owner.UserId
        };

        await context.Set<CommandLogEntry>().AddAsync(logEntry, cancellationToken);
        return commandId;
    }
}
