using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Repositories;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore;

/// <summary>
/// Write-side unit of work that mints repositories for the event-sourcing tables —
/// <see cref="IEventStreamRepository"/>, <see cref="IEventChainRepository"/>,
/// <see cref="ISnapshotRepository"/>, <see cref="ICommandAuditRepository"/>, and
/// <see cref="IOutboxRepository"/> — all sharing the same EF Core transaction.
/// </summary>
/// <typeparam name="TDbContext">The concrete write-store DbContext type.</typeparam>
/// <param name="contextFactory">Factory used to create a new DbContext per transaction.</param>
/// <param name="sessionContextProvider">Provides the ambient session context used by the command-audit repository.</param>
/// <param name="serializer">Secure JSON serializer used by the command-audit repository to encrypt command payloads.</param>
public class WriteUnitOfWork<TDbContext>(
    IDbContextFactory<TDbContext> contextFactory,
    ISessionContextProvider sessionContextProvider,
    ISecureJsonSerializer serializer)
    : UnitOfWork<TDbContext>(contextFactory), IWriteUnitOfWork where TDbContext : DbContext, IWriteDbContext
{
    /// <inheritdoc/>
    public IEventStreamRepository CreateEventStreamRepository(ITransaction transaction)
    {
        var dbContext = GetDbContext(transaction);
        return new EventStreamRepository(dbContext);
    }

    /// <inheritdoc/>
    public IEventChainRepository CreateEventChainRepository(ITransaction transaction)
    {
        var dbContext = GetDbContext(transaction);
        return new EventChainRepository(dbContext);
    }

    /// <inheritdoc/>
    public ISnapshotRepository CreateSnapshotRepository(ITransaction transaction)
    {
        var dbContext = GetDbContext(transaction);
        return new SnapshotRepository(dbContext);
    }

    /// <inheritdoc/>
    public ICommandAuditRepository CreateCommandAuditRepository(ITransaction transaction)
    {
        var dbContext = GetDbContext(transaction);
        return new CommandAuditRepository(dbContext, sessionContextProvider, serializer);
    }

    /// <inheritdoc/>
    public IOutboxRepository CreateOutboxRepository(ITransaction transaction)
    {
        var dbContext = GetDbContext(transaction);
        return new OutboxRepository(dbContext);
    }

}
