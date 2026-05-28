namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Repository over the event-chain anchor table — periodic global hashes that link the
/// per-stream hash chain into a tamper-evident sequence.
/// </summary>
public interface IEventChainRepository
{
    /// <summary>
    /// Returns the most-recent sequence number that is already covered by an anchor for
    /// any of the given <paramref name="bucketIds"/>, or <c>0</c> if no anchor exists yet.
    /// </summary>
    Task<long> GetLastSequenceNumberOrDefaultAsync(int[] bucketIds, CancellationToken cancellationToken = default);

    /// <summary>Persist a new anchor row. Caller owns the transactional save.</summary>
    Task AddAnchorAsync(EventChainAnchor anchor, CancellationToken cancellationToken);
}
