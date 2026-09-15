using Stratara.Abstractions.Outbox;

namespace Stratara.Abstractions.Outbox;

/// <summary>
/// Repository over the <c>outbox_entry</c> table — durable queue of messages that
/// couldn't be published directly to the bus and are retried by the outbox worker.
/// </summary>
public interface IOutboxRepository
{
    /// <summary>Persist an outbox entry for later delivery.</summary>
    /// <typeparam name="T">The serialised payload type (command envelope or event bundle).</typeparam>
    Task AddAsync<T>(T outboxData, CancellationToken cancellationToken);

    /// <summary>
    /// Persist an outbox entry under an identity the caller chose, so the caller can remove it
    /// later by that identity. Used by a dispatcher that stores a bundle with the commit and
    /// deletes it once the bus has accepted it.
    /// </summary>
    /// <typeparam name="T">The serialised payload type (command envelope or event bundle).</typeparam>
    /// <param name="id">The identity of the entry.</param>
    /// <param name="outboxData">The payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="NotSupportedException">The repository does not support caller-chosen identities.</exception>
    Task AddAsync<T>(Guid id, T outboxData, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This outbox repository does not support caller-chosen entry identities.");

    /// <summary>Remove an entry once it has been published successfully.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Removes a set of entries in one call.</summary>
    /// <remarks>
    /// The default removes the entries one at a time through <see cref="DeleteAsync"/>, so an
    /// implementation written before this member keeps working unchanged. An implementation backed by
    /// a database overrides it with a single statement.
    /// </remarks>
    /// <param name="ids">The identities of the entries; an identity that matches no entry is ignored.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when every entry is removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> is <see langword="null"/>.</exception>
    async Task DeleteManyAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            await DeleteAsync(id, cancellationToken);
        }
    }

    /// <summary>Return up to <paramref name="batchSizes"/> oldest entries of type <typeparamref name="T"/>.</summary>
    Task<IReadOnlyList<OutboxEntry>> GetManyAsync<T>(int batchSizes, CancellationToken cancellationToken);
}
