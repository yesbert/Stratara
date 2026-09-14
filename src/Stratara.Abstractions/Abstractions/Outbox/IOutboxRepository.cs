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

    /// <summary>Return up to <paramref name="batchSizes"/> oldest entries of type <typeparamref name="T"/>.</summary>
    Task<IReadOnlyList<OutboxEntry>> GetManyAsync<T>(int batchSizes, CancellationToken cancellationToken);
}
