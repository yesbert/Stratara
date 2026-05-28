using Stratara.Abstractions.Mediator;

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Persists every dispatched command for audit purposes. The <c>CommandAuditBehavior</c>
/// pipeline-behavior invokes this; the returned id is propagated to subsequent event
/// appends as the <c>CausationId</c>.
/// </summary>
public interface ICommandAuditRepository
{
    /// <summary>
    /// Persist <paramref name="command"/> as an audit row.
    /// </summary>
    /// <param name="command">The command being dispatched.</param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    /// <returns>The id assigned to the audit row. Used as <c>CausationId</c> downstream.</returns>
    Task<Guid> AddAsync(ICommandBase command, CancellationToken cancellationToken);
}
