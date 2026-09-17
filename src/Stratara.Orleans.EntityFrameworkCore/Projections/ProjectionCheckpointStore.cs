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
    /// insert after it. Two writers of the same first checkpoint both find no row; the one whose insert
    /// loses to the key updates the row the other inserted instead of failing. A row held under another
    /// reader is refused, as a read under it is.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The checkpoint was written under a different reader.</exception>
    public async Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await UpdateAsync(context, projection, partition, reader, expected: null, position, cancellationToken))
        {
            return;
        }

        if (await FindAsync(context, projection, partition, cancellationToken) is { } found)
        {
            // Another writer inserted the row between the update and the read; under this reader it is replaced all the same.
            if (found.Reader == reader && await UpdateAsync(context, projection, partition, reader, expected: null, position, cancellationToken))
            {
                return;
            }

            throw new InvalidOperationException(Refusal(projection, partition, found.Reader, reader));
        }

        await InsertAsync(context, projection, partition, reader, expected: null, position, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One conditional statement in the steady state: the row is updated only where it still holds
    /// <paramref name="from"/> under <paramref name="reader"/>. A write that changes nothing reads the row to say
    /// why; a checkpoint never written is inserted when <paramref name="from"/> is <c>0</c>, and a writer that loses
    /// that insert to another is held to the same condition against the row the other inserted.
    /// </remarks>
    public async Task AdvanceAsync(string projection, int partition, string reader, long from, long to, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await UpdateAsync(context, projection, partition, reader, from, to, cancellationToken))
        {
            return;
        }

        if (await FindAsync(context, projection, partition, cancellationToken) is { } found)
        {
            throw new InvalidOperationException(Refusal(projection, partition, found, reader, from));
        }

        if (from != 0)
        {
            throw new InvalidOperationException($"The checkpoint of {projection}/{partition} does not exist, and the reader expected it at {from}. Another writer removed it; the reader reads the checkpoint again.");
        }

        await InsertAsync(context, projection, partition, reader, from, to, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The one write that is not held to the reader's name: the beginning is the beginning under every reader and
    /// every partition count, so a rebuild or a replay on a host that reads under a new name takes the row over
    /// instead of being refused by the guard its own message asks the operator to clear.
    /// </remarks>
    public async Task ResetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await ResetRowAsync(context, projection, partition, reader, cancellationToken))
        {
            return;
        }

        context.Set<ProjectionCheckpoint>().Add(new ProjectionCheckpoint { Projection = projection, Partition = partition, Position = 0, Reader = reader });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another writer inserted the row between the update and the insert — under any reader's name, which the
            // reset takes over like any other.
            context.ChangeTracker.Clear();
            if (!await ResetRowAsync(context, projection, partition, reader, cancellationToken))
            {
                throw;
            }
        }
    }

    /// <summary>Returns the row to the beginning under <paramref name="reader"/>, whatever reader holds it.</summary>
    private static async Task<bool> ResetRowAsync(TContext context, string projection, int partition, string reader, CancellationToken cancellationToken)
    {
        var rows = await context.Set<ProjectionCheckpoint>()
            .Where(c => c.Projection == projection && c.Partition == partition)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Position, 0L).SetProperty(c => c.Reader, reader), cancellationToken);
        return rows > 0;
    }

    private async Task InsertAsync(TContext context, string projection, int partition, string reader, long? expected, long position, CancellationToken cancellationToken)
    {
        context.Set<ProjectionCheckpoint>().Add(new ProjectionCheckpoint { Projection = projection, Partition = partition, Position = position, Reader = reader });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if (await UpdateAsync(context, projection, partition, reader, expected, position, cancellationToken))
            {
                return;
            }

            if (await FindAsync(context, projection, partition, cancellationToken) is { } found)
            {
                throw new InvalidOperationException(expected is { } from
                    ? Refusal(projection, partition, found, reader, from)
                    : Refusal(projection, partition, found.Reader, reader));
            }

            throw;
        }
    }

    private static string Refusal(string projection, int partition, ProjectionCheckpoint found, string reader, long expected) =>
        found.Reader != reader
            ? Refusal(projection, partition, found.Reader, reader)
            : $"The checkpoint of {projection}/{partition} is at {found.Position}, not at {expected} where this reader last saw it. Another activation of the reader advanced it; the reader reads the checkpoint again.";

    private static Task<ProjectionCheckpoint?> FindAsync(TContext context, string projection, int partition, CancellationToken cancellationToken) =>
        context.Set<ProjectionCheckpoint>().AsNoTracking()
            .SingleOrDefaultAsync(c => c.Projection == projection && c.Partition == partition, cancellationToken);

    private static async Task<bool> UpdateAsync(TContext context, string projection, int partition, string reader, long? expected, long position, CancellationToken cancellationToken)
    {
        var rows = context.Set<ProjectionCheckpoint>()
            .Where(c => c.Projection == projection && c.Partition == partition && c.Reader == reader);
        if (expected is { } from)
        {
            rows = rows.Where(c => c.Position == from);
        }

        var updated = await rows.ExecuteUpdateAsync(set => set.SetProperty(c => c.Position, position), cancellationToken);
        return updated == 1;
    }
}
