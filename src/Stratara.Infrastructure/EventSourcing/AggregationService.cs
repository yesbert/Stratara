using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.Reflections;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Default <see cref="IAggregationService"/> implementation that rebuilds aggregate state from the
/// event stream, optionally seeded with the latest snapshot.
/// </summary>
/// <remarks>
/// When a snapshot exists for the stream, it is deserialized via the configured
/// <see cref="ISecureJsonSerializer"/> under the owner it records — tenant and user — and the
/// remaining events on top of the snapshot version are applied. Without a snapshot, the aggregate is built by replaying the full event stream.
/// Only the entries <see cref="AggregateEventSelector"/> selects are mapped, so an event the aggregate
/// has no <c>Apply</c> for is neither resolved nor decrypted. Where the host asks for appends conditional on the
/// aggregated version, a rebuild without an upper bound records the version the stream was at — the last entry
/// read, whether or not the aggregate applies it, the snapshot's version when no entry follows it, or 0 for a
/// stream that does not exist — in <see cref="AggregatedStreamVersions"/>, which the event source appends
/// against. Without that option nothing is recorded.
/// </remarks>
internal sealed class AggregationService(
    IWriteUnitOfWork unitOfWork,
    IEventMapperFactory eventMapperFactory,
    ISecureJsonSerializer serializer,
    AggregateEventSelector eventSelector,
    AggregatedStreamVersions? aggregatedVersions = null,
    IOptions<EventSourcingOptions>? options = null) : IAggregationService
{
    private readonly AggregatedStreamVersions? _aggregatedVersions =
        options?.Value.AppendAgainstAggregatedVersion == true ? aggregatedVersions : null;

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
            RecordHead(streamId, toVersion, 0);
            return null;
        }

        var snapshotRepository = unitOfWork.CreateSnapshotRepository(transaction);
        var snapshot = await snapshotRepository.GetAsync(streamId, aggregateType.GetQualifiedTypeName(), toVersion, cancellationToken);
        var snapshotVersion = snapshot?.Version + 1 ?? 0;

        var eventStreamEntries = await eventStreamRepository.GetManyAsync(streamId, snapshotVersion, toVersion, cancellationToken);
        RecordHead(streamId, toVersion, eventStreamEntries.Count > 0 ? eventStreamEntries[^1].Version : snapshot?.Version ?? 0);
        var events = await eventMapperFactory.MapToEventsAsync(eventSelector.Select(aggregateType, eventStreamEntries), cancellationToken);

        if (snapshot is null)
        {
            return EventStream.Aggregate(aggregateType, events);
        }

        var aggregate = await serializer.DeserializeAsync(snapshot.DataJson, aggregateType, snapshot.TenantId, snapshot.UserId, cancellationToken) ??
                        throw new InvalidOperationException($"Could not deserialize snapshot of type {aggregateType.Name} with ID {streamId}");

        aggregate.ApplyEvents(events);
        return aggregate;
    }

    private void RecordHead(Guid streamId, long? toVersion, long head)
    {
        if (toVersion is null)
        {
            _aggregatedVersions?.Record(streamId, head);
        }
    }
}
