using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Stratara.Contracts.Messages;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;

namespace Stratara.Shared.EventSourcing.Mapping;

/// <summary>
/// Default <see cref="IEventMapperFactory"/> implementation. Materializes typed <see cref="IEvent"/>
/// instances from persisted <see cref="EventStreamEntry"/> rows or wire-level
/// <see cref="EventMessage"/> envelopes by resolving the runtime event type, decrypting / deserializing
/// the JSON payload through <see cref="ISecureJsonSerializer"/>, and constructing
/// <see cref="Event{TEvent}"/> via a per-type cached compiled lambda factory.
/// </summary>
/// <remarks>
/// The factory cache is process-wide and unbounded; the framework assumes a finite set of event
/// types per host. AAD on the encrypted payload uses the subject (data-owner) tenant id, which
/// matches <see cref="EventStreamEntry.TenantId"/>.
/// </remarks>
public sealed class EventMapperFactory(ISecureJsonSerializer serializer, ITrustedTypeResolver typeResolver) : IEventMapperFactory
{
    private static readonly ConcurrentDictionary<Type, EventFactoryDelegate> s_factoryCache = new();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventStreamEntry> entries, CancellationToken cancellationToken = default)
    {
        var result = new List<IEvent>();
        foreach (var entry in entries)
        {
            result.Add(await MapToEventAsync(entry, cancellationToken));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventMessage> messages, CancellationToken cancellationToken = default)
    {
        var result = new List<IEvent>();
        foreach (var message in messages)
        {
            result.Add(await MapToEventEnvelopesAsync(message, cancellationToken));
        }
        return result;
    }

    private async Task<IEvent> MapToEventAsync(EventStreamEntry entry, CancellationToken cancellationToken)
    {
        var eventType = typeResolver.Resolve(entry.EventTypeName);
        var data = await serializer.DeserializeAsync(entry.DataJson, eventType, entry.TenantId, entry.UserId, cancellationToken) ??
                   throw new InvalidOperationException("Event data could not be deserialized.");

        var factory = s_factoryCache.GetOrAdd(eventType, static type => CreateEventFactory(type));
        return factory(entry.Id, entry.Version, data, entry.StreamId, entry.TenantId, entry.ActorUserId, entry.AggregateTypeName);
    }

    private async Task<IEvent> MapToEventEnvelopesAsync(EventMessage message, CancellationToken cancellationToken)
    {
        var eventType = typeResolver.Resolve(message.EventTypeName);
        var data = await serializer.DeserializeAsync(message.DataJson, eventType, message.TenantId, message.UserId, cancellationToken) ??
                   throw new InvalidOperationException("Event data could not be deserialized.");

        var factory = s_factoryCache.GetOrAdd(eventType, static type => CreateEventFactory(type));
        return factory(message.Id, message.Version, data, message.StreamId, message.TenantId, message.ActorUserId, message.AggregateTypeName);
    }

    private static EventFactoryDelegate CreateEventFactory(Type eventDataType)
    {
        var genericEventType = typeof(Event<>).MakeGenericType(eventDataType);
        var ctor = GetEventConstructor(genericEventType, typeof(Guid), typeof(long), eventDataType, typeof(Guid), typeof(Guid), typeof(Guid), typeof(string));

        if (ctor is null)
        {
            throw new InvalidOperationException($"No matching constructor for {genericEventType.Name}");
        }

        var parameters = CreateParameterExpressions();
        var castedData = Expression.Convert(parameters.dataParam, eventDataType);
        var newExpr = Expression.New(ctor, parameters.idParam, parameters.versionParam, castedData, parameters.streamIdParam,
            parameters.tenantIdParam, parameters.userIdParam, parameters.aggregateTypeNameParam);

        return Expression.Lambda<EventFactoryDelegate>(newExpr,
            parameters.idParam, parameters.versionParam, parameters.dataParam, parameters.streamIdParam, parameters.tenantIdParam, parameters.userIdParam,
            parameters.aggregateTypeNameParam).Compile();
    }

    private static ConstructorInfo? GetEventConstructor(Type genericEventType, Type idType, Type versionType, Type dataType, Type streamIdType, // NOSONAR — 8 parameters required to match the Event<T> constructor signature exactly
        Type tenantIdType, Type userIdType, Type aggregateTypeNameType) =>
        genericEventType.GetConstructor([idType, versionType, dataType, streamIdType, tenantIdType, userIdType, aggregateTypeNameType]);

    private static (ParameterExpression idParam, ParameterExpression versionParam, ParameterExpression dataParam, ParameterExpression streamIdParam,
        ParameterExpression tenantIdParam, ParameterExpression userIdParam, ParameterExpression aggregateTypeNameParam) CreateParameterExpressions() =>
    (
        Expression.Parameter(typeof(Guid), "id"),
        Expression.Parameter(typeof(long), "version"),
        Expression.Parameter(typeof(object), "data"),
        Expression.Parameter(typeof(Guid), "streamId"),
        Expression.Parameter(typeof(Guid), "tenantId"),
        Expression.Parameter(typeof(Guid), "userId"),
        Expression.Parameter(typeof(string), "aggregateTypeName")
    );

    private delegate IEvent EventFactoryDelegate(Guid id, long version, object data, Guid streamId, Guid tenantId, Guid userId, string? aggregateTypeName);
}
