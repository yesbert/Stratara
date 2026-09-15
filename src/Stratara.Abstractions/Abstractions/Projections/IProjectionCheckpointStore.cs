namespace Stratara.Abstractions.Projections;

/// <summary>
/// Reads and writes the position a store-reading consumer resumes after, one per consumer and
/// partition. A position is only meaningful to the reader that produced it, so every read names the
/// reader it expects and a checkpoint written under another reader is refused rather than misread.
/// </summary>
public interface IProjectionCheckpointStore
{
    /// <summary>Returns the stored position, or <c>0</c> where none exists.</summary>
    /// <param name="projection">The consumer, by the name the framework gives it.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="reader">The name of the reader whose positions are expected.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>The position to resume after.</returns>
    /// <exception cref="InvalidOperationException">A checkpoint exists but was written under a different reader.</exception>
    Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default);

    /// <summary>Stores a position, replacing the previous one.</summary>
    /// <param name="projection">The consumer, by the name the framework gives it.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="reader">The name of the reader whose position this is.</param>
    /// <param name="position">The position to resume after.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>A task that completes when the position is stored.</returns>
    Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default);
}
