using Microsoft.EntityFrameworkCore;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Where one projection's grain resumes in one partition, and which reader's positions it holds. A
/// position from one reader means nothing to another, so a checkpoint written under a different
/// reader is refused rather than misread.
/// </summary>
public sealed class ProjectionCheckpoint
{
    /// <summary>The projection, by the name the framework gives it.</summary>
    public required string Projection { get; set; }

    /// <summary>The partition.</summary>
    public int Partition { get; set; }

    /// <summary>The position to resume after.</summary>
    public long Position { get; set; }

    /// <summary>The reader whose positions these are.</summary>
    public required string Reader { get; set; }
}

/// <summary>Adds the checkpoint table to a read-store model.</summary>
public static class ProjectionCheckpointModel
{
    /// <summary>The checkpoint table.</summary>
    public const string Table = "projection_checkpoint";

    /// <summary>Adds the checkpoint entity to <paramref name="modelBuilder"/>.</summary>
    /// <param name="modelBuilder">The read context's model builder, after the base model.</param>
    public static void Apply(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ProjectionCheckpoint>(checkpoint =>
        {
            checkpoint.ToTable(Table);
            checkpoint.HasKey(c => new { c.Projection, c.Partition });
            checkpoint.Property(c => c.Projection).HasMaxLength(255);
            checkpoint.Property(c => c.Reader).HasMaxLength(255);
        });
    }
}

/// <summary>Reads and writes projection checkpoints.</summary>
public interface IProjectionCheckpointStore
{
    /// <summary>The stored position, or <c>0</c> where none exists.</summary>
    /// <param name="projection">The projection.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="reader">The reader whose positions are expected.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <exception cref="InvalidOperationException">A checkpoint exists but was written under a different reader.</exception>
    Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default);

    /// <summary>Stores a position, replacing the previous one.</summary>
    /// <param name="projection">The projection.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="reader">The reader whose position this is.</param>
    /// <param name="position">The position to resume after.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default);
}

/// <summary>Checkpoints in the read store, one row per projection and partition.</summary>
/// <typeparam name="TContext">The read context, with <see cref="ProjectionCheckpointModel"/> applied.</typeparam>
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
            throw new InvalidOperationException(
                $"The checkpoint of {projection}/{partition} was written by reader '{checkpoint.Reader}', not '{reader}'. Positions are not comparable across readers; reset the checkpoint before switching.");
        }

        return checkpoint.Position;
    }

    /// <inheritdoc/>
    public async Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var checkpoint = await context.Set<ProjectionCheckpoint>()
            .SingleOrDefaultAsync(c => c.Projection == projection && c.Partition == partition, cancellationToken);
        if (checkpoint is null)
        {
            context.Set<ProjectionCheckpoint>().Add(new ProjectionCheckpoint { Projection = projection, Partition = partition, Position = position, Reader = reader });
        }
        else
        {
            checkpoint.Position = position;
            checkpoint.Reader = reader;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
