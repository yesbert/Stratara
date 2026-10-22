using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Projections;

/// <summary>
/// Lets a host wait until a projection that reads the store from a checkpoint has applied what was committed: take a
/// <see cref="StoreMark"/> once the write has committed, then wait for the projection to reach it before reading its
/// read model.
/// </summary>
/// <example>
/// <code>
/// await mediator.HandleAsync(command, cancellationToken);
/// var mark = await progress.MarkAsync(cancellationToken);
/// var result = await progress.WaitAsync(typeof(OrdersProjection), mark, TimeSpan.FromSeconds(5), cancellationToken);
/// if (!result.IsReached)
/// {
///     // refuse with "try again", or answer without the read
/// }
/// </code>
/// </example>
public interface IProjectionProgress
{
    /// <summary>
    /// Takes a mark that covers every entry committed at the time of the call, in every partition — an entry that is
    /// committed while another write is still open included. An entry whose transaction commits afterwards is not
    /// covered.
    /// </summary>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>The mark, to be passed back to <see cref="WaitAsync(string, StoreMark, TimeSpan, CancellationToken)"/>.</returns>
    Task<StoreMark> MarkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until the projection has applied every entry <paramref name="mark"/> covers, in every partition, or until
    /// <paramref name="timeout"/> has passed. A partition stopped at an entry does not end the wait early, because a
    /// retry may still apply the entry in time; a rebuild or a full replay during the wait is waited through.
    /// </summary>
    /// <param name="projectionName">The projection, by the name the framework gives it.</param>
    /// <param name="mark">A mark taken with <see cref="MarkAsync"/>.</param>
    /// <param name="timeout">How long to wait at most; <see cref="TimeSpan.Zero"/> answers without waiting.</param>
    /// <param name="cancellationToken">Ends the wait without an answer.</param>
    /// <returns>
    /// <see cref="ProjectionWaitOutcome.Reached"/> once the projection has applied the mark's entries; otherwise why it
    /// has not, with the partitions that are behind.
    /// </returns>
    /// <exception cref="InvalidOperationException">No projection of that name is registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ProjectionWaitResult> WaitAsync(string projectionName, StoreMark mark, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until the projection of <paramref name="projectionType"/> has applied every entry <paramref name="mark"/>
    /// covers; otherwise as <see cref="WaitAsync(string, StoreMark, TimeSpan, CancellationToken)"/>.
    /// </summary>
    /// <param name="projectionType">The type of a registered projection.</param>
    /// <param name="mark">A mark taken with <see cref="MarkAsync"/>.</param>
    /// <param name="timeout">How long to wait at most; <see cref="TimeSpan.Zero"/> answers without waiting.</param>
    /// <param name="cancellationToken">Ends the wait without an answer.</param>
    /// <returns>Whether the projection reached the mark, and if not, why and in which partitions.</returns>
    /// <exception cref="InvalidOperationException">No projection of that type is registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ProjectionWaitResult> WaitAsync(Type projectionType, StoreMark mark, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where the store was when a mark was taken. A host passes it back to <see cref="IProjectionProgress"/> and relies on
/// nothing inside it; what it holds is meaningful only to the reader that produced it. Two marks are equal when they
/// name the same reader and the same positions.
/// </summary>
/// <param name="Reader">The name of the reader the positions belong to, which carries the partition count.</param>
/// <param name="Positions">Per partition, the highest position committed when the mark was taken.</param>
public sealed record StoreMark(string Reader, IReadOnlyList<long> Positions)
{
    /// <summary>Whether <paramref name="other"/> names the same reader and the same positions.</summary>
    /// <param name="other">The mark to compare with.</param>
    /// <returns><see langword="true"/> where reader and positions are equal.</returns>
    public bool Equals(StoreMark? other) =>
        other is not null && Reader == other.Reader && Positions.SequenceEqual(other.Positions);

    /// <summary>A hash over the reader and the positions.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Reader, StringComparer.Ordinal);
        foreach (var position in Positions)
        {
            hash.Add(position);
        }

        return hash.ToHashCode();
    }
}

/// <summary>How a wait for a projection ended, or how a partition stood when it ended.</summary>
public enum ProjectionWaitOutcome
{
    /// <summary>The projection has applied every entry the mark covers.</summary>
    Reached,

    /// <summary>The time passed while the projection was still behind and advancing.</summary>
    TimedOut,

    /// <summary>The time passed while a partition was stopped at an entry it cannot apply, or could not read the store.</summary>
    Stalled,

    /// <summary>The time passed while a partition's readers were paused for a rebuild or a full replay was active.</summary>
    Suspended,

    /// <summary>The mark was taken under another reader or partition count than the host reads with now.</summary>
    MarkNotApplicable,
}

/// <summary>A partition that did not reach the mark, and how it stood when the wait ended.</summary>
/// <param name="Partition">The partition.</param>
/// <param name="State">How it stood: <see cref="ProjectionWaitOutcome.TimedOut"/>, <see cref="ProjectionWaitOutcome.Stalled"/> or <see cref="ProjectionWaitOutcome.Suspended"/>.</param>
[ExcludeFromCodeCoverage]
public sealed record ProjectionPartitionLag(int Partition, ProjectionWaitOutcome State);

/// <summary>The answer of a wait for a projection.</summary>
/// <param name="Outcome">
/// <see cref="ProjectionWaitOutcome.Reached"/> where the projection got there; otherwise the most telling state of the
/// partitions behind — stalled before suspended before timed out — or <see cref="ProjectionWaitOutcome.MarkNotApplicable"/>.
/// </param>
/// <param name="Behind">The partitions that did not reach the mark; empty where it was reached or where the mark does not apply.</param>
[ExcludeFromCodeCoverage]
public sealed record ProjectionWaitResult(ProjectionWaitOutcome Outcome, IReadOnlyList<ProjectionPartitionLag> Behind)
{
    /// <summary>Whether the projection has applied every entry the mark covers.</summary>
    public bool IsReached => Outcome == ProjectionWaitOutcome.Reached;
}
