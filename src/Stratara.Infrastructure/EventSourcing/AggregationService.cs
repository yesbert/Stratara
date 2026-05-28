using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Default <see cref="IAggregationService"/> implementation that rebuilds aggregate state from the
/// event stream, optionally seeded with the latest snapshot.
/// </summary>
/// <remarks>
/// When a snapshot exists for the stream, it is deserialized via the configured
/// <see cref="ISecureJsonSerializer"/> (tenant-scoped AAD) and remaining events on top of the snapshot
/// version are applied. Without a snapshot, the aggregate is built by replaying the full event stream.
/// </remarks>
internal sealed class AggregationService(
    IWriteUnitOfWork unitOfWork,
    IEventMapperFactory eventMapperFactory,
    ISecureJsonSerializer serializer) : IAggregationService
{
    /// <inheritdoc/>
    public async Task<TAggregate?> AggregateAsync<TAggregate>(Guid streamId, long? fromVersion = null,
        long? toVersion = null, CancellationToken cancellationToken = default) where TAggregate : notnull, new()
    {
        var aggregateType = typeof(TAggregate);
        var aggregate = await AggregateAsync(aggregateType, streamId, fromVersion, toVersion, cancellationToken);
        return (TAggregate?)aggregate;
    }

    /// <inheritdoc/>
    public async Task<object?> AggregateAsync(Type aggregateType, Guid streamId, long? fromVersion = null, long? toVersion = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);

        if (!await eventStreamRepository.StreamExistsAsync(streamId, cancellationToken))
        {
            return null;
        }

        var snapshotRepository = unitOfWork.CreateSnapshotRepository(transaction);
        var snapshot = await snapshotRepository.GetAsync(streamId, toVersion, cancellationToken);
        var snapshotVersion = snapshot?.Version + 1 ?? 0;

        var eventStreamEntries = await eventStreamRepository.GetManyAsync(streamId, snapshotVersion, toVersion, cancellationToken);
        var events = await eventMapperFactory.MapToEventsAsync(eventStreamEntries, cancellationToken);

        if (snapshot is null)
        {
            return EventStream.Aggregate(aggregateType, events);
        }

        var aggregate = await serializer.DeserializeAsync(snapshot.DataJson, aggregateType, snapshot.TenantId, cancellationToken: cancellationToken) ??
                        throw new InvalidOperationException($"Could not deserialize snapshot of type {aggregateType.Name} with ID {streamId}");

        aggregate.ApplyEvents(events);
        return aggregate;
    }
}
