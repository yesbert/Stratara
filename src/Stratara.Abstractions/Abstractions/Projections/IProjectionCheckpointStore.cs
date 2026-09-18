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

    /// <summary>Stores a position, replacing the previous one — the verb a rebuild, a reset or a seeding writes with.</summary>
    /// <param name="projection">The consumer, by the name the framework gives it.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="reader">The name of the reader whose position this is.</param>
    /// <param name="position">The position to resume after.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>A task that completes when the position is stored.</returns>
    Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances a position from the one its writer last saw — the verb a reader writes with after it applied a batch.
    /// </summary>
    /// <param name="projection">The consumer, by the name the framework gives it.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="reader">The name of the reader whose position this is.</param>
    /// <param name="from">The position the writer last read or wrote; <c>0</c> where it found none.</param>
    /// <param name="to">The position to resume after.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>A task that completes when the position is stored.</returns>
    /// <exception cref="InvalidOperationException">
    /// The stored position is not <paramref name="from"/>, or the checkpoint was written under a different reader.
    /// </exception>
    /// <remarks>
    /// The default replaces the position through <see cref="SetAsync"/> and checks nothing. A store that overrides it
    /// refuses a write that finds another position or another reader, so that an activation which outlived its
    /// successor neither rewinds nor overtakes what the successor wrote; the refused reader reads the checkpoint again.
    /// A rebuild and a full replay rely on that refusal as well: a reader that applied facts while it should have been
    /// paused, and still holds the position it reached before the read model was emptied, is refused once the rebuild
    /// returns the checkpoint to the beginning, and reads those facts again. Under the unguarded default such a reader
    /// writes its position over the beginning and the read model lacks the facts it applied before it was emptied, so a
    /// store used with a rebuild or a replay should override this method; the framework's store does.
    /// </remarks>
    Task AdvanceAsync(string projection, int partition, string reader, long from, long to, CancellationToken cancellationToken = default) =>
        SetAsync(projection, partition, reader, to, cancellationToken);

    /// <summary>
    /// Returns a position to the beginning — the verb a rebuild and a full replay write with.
    /// </summary>
    /// <param name="projection">The consumer, by the name the framework gives it.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="reader">The name of the reader the consumer will read under from now on.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>A task that completes when the checkpoint is at the beginning.</returns>
    /// <remarks>
    /// The beginning means the same under every reader and every partition count, so a store that refuses a write
    /// under another reader's name accepts this one and takes the row over: it is how a deployment whose reader or
    /// partition count changed rebuilds without being stopped. The default writes through <see cref="SetAsync"/> and
    /// therefore takes nothing over — a store that guards the reader's name overrides this, as the framework's does.
    /// </remarks>
    Task ResetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default) =>
        SetAsync(projection, partition, reader, 0, cancellationToken);
}
