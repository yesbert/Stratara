using Stratara.Abstractions.Projections;
using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;

namespace Stratara.Orleans.EntityFrameworkCore.Projections;

/// <summary>Checkpoints in the read store, one row per projection and partition.</summary>
/// <typeparam name="TContext">A read context derived from the framework's read context, which declares the checkpoint table.</typeparam>
public sealed class ProjectionCheckpointStore<TContext>(IDbContextFactory<TContext> contextFactory) : IProjectionCheckpointStore
    where TContext : DbContext
{
    /// <inheritdoc/>
    public async Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var checkpoint = await context.Set<ProjectionCheckpoint>().AsNoTracking()
            .SingleOrDefaultAsync(c => c.Projection == projection && c.Partition == partition, cancellationToken);
        if (checkpoint is null)
        {
            return 0;
        }

        if (checkpoint.Reader != reader)
        {
            throw new InvalidOperationException(Refusal(projection, partition, checkpoint.Reader, reader));
        }

        return checkpoint.Position;
    }

    /// <summary>
    /// Names what differs: the partition count, when the same reader wrote the checkpoint under
    /// another count, or the two readers otherwise.
    /// </summary>
    private static string Refusal(string projection, int partition, string written, string expected)
    {
        var (writtenKind, writtenCount) = Split(written);
        var (expectedKind, expectedCount) = Split(expected);
        return writtenKind == expectedKind && writtenCount != expectedCount
            ? $"The checkpoint of {projection}/{partition} was written under a partition count of {writtenCount}, and the host now reads under {expectedCount}. Positions are not comparable across partition counts; reset the checkpoints before changing the count."
            : $"The checkpoint of {projection}/{partition} was written by reader '{written}', not '{expected}'. Positions are not comparable across readers; reset the checkpoint before switching.";
    }

    private static (string Kind, string Count) Split(string readerName)
    {
        var separator = readerName.LastIndexOf('/');
        return separator < 0 ? (readerName, string.Empty) : (readerName[..separator], readerName[(separator + 1)..]);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One statement in the steady state: the row exists after the first write, and an update that
    /// touches it is the whole round trip. Only a checkpoint that has never been written costs the
    /// insert after it.
    /// </remarks>
    public async Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await context.Set<ProjectionCheckpoint>()
            .Where(c => c.Projection == projection && c.Partition == partition)
            .ExecuteUpdateAsync(
                set => set.SetProperty(c => c.Position, position).SetProperty(c => c.Reader, reader),
                cancellationToken);
        if (updated == 1)
        {
            return;
        }

        context.Set<ProjectionCheckpoint>().Add(new ProjectionCheckpoint { Projection = projection, Partition = partition, Position = position, Reader = reader });
        await context.SaveChangesAsync(cancellationToken);
    }
}
