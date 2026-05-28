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

    /// <summary>Remove an entry once it has been published successfully.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Return up to <paramref name="batchSizes"/> oldest entries of type <typeparamref name="T"/>.</summary>
    Task<IReadOnlyList<OutboxEntry>> GetManyAsync<T>(int batchSizes, CancellationToken cancellationToken);
}
