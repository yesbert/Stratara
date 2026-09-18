using Stratara.Abstractions.EventSourcing;

namespace Stratara.Abstractions.CommitOrder;

/// <summary>
/// Reads the event store in commit order, one partition at a time. The promise that distinguishes
/// it from a plain "after sequence number" read: no entry whose position is at or below the
/// <see cref="CommittedBatch.Position"/> of a returned batch can still commit later. A reader that
/// keeps that promise can be resumed from a stored position without ever skipping an entry.
/// </summary>
/// <remarks>
/// What a position <em>is</em> depends on the implementation — a sequence number, a per-partition
/// counter, a transaction id — and a stored position is only meaningful to the reader that produced
/// it. A read that orders by sequence number alone does not keep the promise: a transaction can take the
/// lower sequence number and commit after one that took a higher.
/// </remarks>
public interface ICommittedPositionReader
{
    /// <summary>
    /// The stable name positions are stored under. It names the ordering the positions belong to and
    /// the partition count they were written under, so a checkpoint is refused by a reader of another
    /// kind or under another count, and renaming a class changes nothing a checkpoint is keyed on.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Returns up to <paramref name="batchSize"/> entries of <paramref name="partition"/> that come
    /// after <paramref name="afterPosition"/> in commit order, together with the position to resume
    /// from.
    /// </summary>
    /// <param name="partition">The partition to read, from <c>0</c> to the configured partition count − 1.</param>
    /// <param name="afterPosition">The position returned by the previous batch, or <c>0</c> to start from the beginning.</param>
    /// <param name="batchSize">The maximum number of entries to return.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>The entries in commit order, and the position to store; an empty batch keeps the position it was asked for.</returns>
    Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the head of <paramref name="partition"/>: the position after which no entry committed at the time of
    /// the call exists, so that a consumer starting there reads exactly what commits afterwards. <c>0</c> on a
    /// partition without entries. The default walks the partition batch by batch from the beginning and returns
    /// the last position; a reader overrides it with one query where its store can answer directly.
    /// </summary>
    /// <remarks>
    /// A reader whose positions come from the store's own transactions — the native reader on PostgreSQL — cannot
    /// count what a write transaction still open might yet commit before the entries it already sees, so its head is
    /// held back to what no open transaction can precede. Take a head while nothing appends, which is what seeding a
    /// deployment does; taken while writers run, it may fall behind what is committed, and a consumer seeded at it
    /// applies the entries in between once more.
    /// </remarks>
    /// <param name="partition">The partition, from <c>0</c> to the configured partition count − 1.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>The position to store for a consumer that starts at the head.</returns>
    async Task<long> HeadAsync(int partition, CancellationToken cancellationToken = default)
    {
        const int walkBatchSize = 1_000;
        var position = 0L;
        while (true)
        {
            var batch = await ReadAfterAsync(partition, position, walkBatchSize, cancellationToken);
            if (batch.Entries.Count == 0)
            {
                return position;
            }

            position = batch.Position;
            if (!batch.HasMore)
            {
                return position;
            }
        }
    }
}

/// <summary>
/// One batch from an <see cref="ICommittedPositionReader"/>: the entries in commit order, each with
/// its own position, and the position to resume from once all of them are applied.
/// </summary>
/// <remarks>
/// Two entries may share a position — one transaction wrote both — and a batch never holds part of
/// such a group. A consumer that applies a batch only partly resumes from the highest position
/// strictly below the first entry it did not apply, which <see cref="ResumePositionBefore"/> computes.
/// </remarks>
/// <param name="Entries">The entries, in the order they are to be applied.</param>
/// <param name="Position">The position to store after applying every entry in <paramref name="Entries"/>.</param>
public sealed record CommittedBatch(IReadOnlyList<CommittedEntry> Entries, long Position)
{
    /// <summary>An empty batch that leaves the position where it was.</summary>
    /// <param name="position">The position the read was asked for.</param>
    /// <returns>A batch with no entries and <paramref name="position"/> unchanged.</returns>
    public static CommittedBatch Empty(long position) => new([], position);

    /// <summary>
    /// Whether the partition held more entries after this batch when it was read. A consumer that has
    /// applied a batch without more stops reading until the next wake-up instead of issuing an empty read.
    /// </summary>
    public bool HasMore { get; init; }

    /// <summary>
    /// The position to resume from when the entries before <paramref name="firstUnappliedIndex"/>
    /// were applied and the rest were not.
    /// </summary>
    /// <param name="firstUnappliedIndex">The index of the first entry that was not applied.</param>
    /// <param name="positionBeforeBatch">The position the batch was read after.</param>
    /// <returns>The highest applied position no unapplied entry shares, or <paramref name="positionBeforeBatch"/>.</returns>
    public long ResumePositionBefore(int firstUnappliedIndex, long positionBeforeBatch)
    {
        if (firstUnappliedIndex <= 0)
        {
            return positionBeforeBatch;
        }

        var unappliedPosition = Entries[firstUnappliedIndex].Position;
        for (var i = firstUnappliedIndex - 1; i >= 0; i--)
        {
            if (Entries[i].Position < unappliedPosition)
            {
                return Entries[i].Position;
            }
        }

        return positionBeforeBatch;
    }
}

/// <summary>An entry and the commit-order position its reader assigns it.</summary>
/// <param name="Entry">The entry.</param>
/// <param name="Position">Its position in the reader's order.</param>
public sealed record CommittedEntry(EventStreamEntry Entry, long Position);
