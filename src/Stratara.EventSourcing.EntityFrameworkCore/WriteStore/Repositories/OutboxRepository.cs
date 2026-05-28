using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.Outbox;
using Stratara.Shared.Partitioning;
using Stratara.Shared.Reflections;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Repositories;

/// <summary>
/// EF Core-backed <see cref="IOutboxRepository"/> over the <c>outbox_entry</c> table. Inserts
/// JSON-serialized payloads, fetches batches by type for the outbox worker, and deletes rows
/// once they have been published.
/// </summary>
/// <remarks>
/// Provides the durable side of Stratara's outbox pattern; combined with the outbox worker this
/// yields at-least-once delivery semantics — downstream consumers must therefore treat
/// dispatched messages as idempotent. Rows are partitioned via <c>BucketId</c> so multiple
/// outbox workers can shard the table without coordination.
/// </remarks>
/// <param name="context">The write-store DbContext that hosts the outbox table.</param>
internal sealed class OutboxRepository(IWriteDbContext context) : IOutboxRepository
{
    /// <inheritdoc/>
    public async Task AddAsync<T>(T outboxData, CancellationToken cancellationToken)
    {
        var dataJson = JsonSerializer.Serialize(outboxData);
        var dataTypeName = typeof(T).GetQualifiedTypeName();
        var id = Guid.CreateVersion7();
        var outboxEntry = new OutboxEntry
        {
            Id = id,
            BucketId = BucketCalculator.GetBucketId(id),
            DataJson = dataJson,
            DataTypeName = dataTypeName,
            Timestamp = DateTime.UtcNow
        };

        await context.Set<OutboxEntry>().AddAsync(outboxEntry, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<OutboxEntry>().Where(o => o.Id == id).ExecuteDeleteAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboxEntry>> GetManyAsync<T>(int batchSizes, CancellationToken cancellationToken)
    {
        var dataTypeName = typeof(T).GetQualifiedTypeName();
        return await context.Set<OutboxEntry>()
            .AsNoTracking()
            .Where(o => o.DataTypeName == dataTypeName)
            .OrderBy(o => o.Timestamp)
            .Take(batchSizes)
            .ToListAsync(cancellationToken);
    }
}
