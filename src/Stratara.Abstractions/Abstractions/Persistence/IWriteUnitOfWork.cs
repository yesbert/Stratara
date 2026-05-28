using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;

namespace Stratara.Abstractions.Persistence;

/// <summary>
/// Write-side unit of work — exposes the repositories that participate in an
/// event-sourced write (event stream, snapshots, hash anchors, command audit, outbox).
/// </summary>
/// <remarks>
/// Each <c>Create*Repository</c> call returns a repository scoped to the supplied
/// <c>transaction</c>; reusing one repository across transactions is
/// undefined behaviour.
/// </remarks>
public interface IWriteUnitOfWork : IUnitOfWork
{
    /// <summary>Repository over <c>event_stream_entry</c> for this transaction.</summary>
    IEventStreamRepository CreateEventStreamRepository(ITransaction transaction);

    /// <summary>Repository over <c>event_chain_anchor</c> for this transaction.</summary>
    IEventChainRepository CreateEventChainRepository(ITransaction transaction);

    /// <summary>Repository over <c>snapshot</c> for this transaction.</summary>
    ISnapshotRepository CreateSnapshotRepository(ITransaction transaction);

    /// <summary>Repository over <c>command_log_entry</c> for this transaction.</summary>
    ICommandAuditRepository CreateCommandAuditRepository(ITransaction transaction);

    /// <summary>Repository over <c>outbox_entry</c> for this transaction.</summary>
    IOutboxRepository CreateOutboxRepository(ITransaction transaction);
}
