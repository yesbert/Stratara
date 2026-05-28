using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;

/// <summary>
/// EF Core-backed <see cref="ISnapshotRepository"/> over the <c>snapshot</c> table. Reads
/// return the latest snapshot at or before a target version; new snapshots are inserted via
/// the surrounding unit of work.
/// </summary>
/// <param name="context">The write-store DbContext that hosts the snapshot table.</param>
internal sealed class SnapshotRepository(IWriteDbContext context) : ISnapshotRepository
{
    /// <inheritdoc/>
    public Task<Snapshot?> GetAsync(Guid streamId, long? toVersion = null, CancellationToken cancellationToken = default) =>
        context.Set<Snapshot>().AsNoTracking()
            .Where(e => e.StreamId == streamId && (!toVersion.HasValue || e.Version <= toVersion))
            .OrderByDescending(e => e.Version)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task AddAsync(Snapshot snapshot, CancellationToken cancellationToken = default)
    {
        await context.Set<Snapshot>().AddAsync(snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<long> GetLatestVersionOrDefaultAsync(Guid streamId, CancellationToken cancellationToken = default) =>
        await context.Set<Snapshot>().AsNoTracking()
            .Where(e => e.StreamId == streamId)
            .OrderByDescending(e => e.Version)
            .Select(e => (long?)e.Version)
            .FirstOrDefaultAsync(cancellationToken) ?? 0L;
}
