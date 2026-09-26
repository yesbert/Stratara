using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Stratara.Contracts.Messages;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Diagnostics;

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
public sealed partial class EventMapperFactory(
    ISecureJsonSerializer serializer,
    ITrustedTypeResolver typeResolver,
    IEventUpcasterPipeline upcasterPipeline,
    ILogger<EventMapperFactory>? logger) : IEventMapperFactory
{
    private static readonly ConcurrentDictionary<Type, EventFactoryDelegate> s_factoryCache = new();
    private const int MaxRememberedNames = 4096;
    private readonly ConcurrentDictionary<string, string> _effectiveNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _skippedUnresolvable = new(StringComparer.Ordinal);
    private readonly bool _upcastsByNameOnly = upcasterPipeline is EventUpcasterPipeline;

    /// <summary>Initializes the mapper without a logger; a skip that would be logged is not.</summary>
    /// <param name="serializer">Decrypts and deserializes the payloads.</param>
    /// <param name="typeResolver">Resolves a recorded type name against the trusted types.</param>
    /// <param name="upcasterPipeline">Upcasts a payload recorded under an older schema.</param>
    public EventMapperFactory(ISecureJsonSerializer serializer, ITrustedTypeResolver typeResolver, IEventUpcasterPipeline upcasterPipeline)
        : this(serializer, typeResolver, upcasterPipeline, null)
    {
    }

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

    /// <inheritdoc/>
    /// <remarks>
    /// An entry is resolved and decrypted only when its type, after upcasting, is relevant — or when the type does not
    /// resolve but carries the name of a relevant type, so that it fails as an unregistered type does. See
    /// <see cref="EventRelevance"/>.
    /// </remarks>
    public async Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventStreamEntry> entries, EventRelevance relevance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(relevance);
        var result = new List<IEvent>();
        foreach (var entry in entries)
        {
            if (Reads(entry.EventTypeName, entry.DataJson, relevance))
            {
                result.Add(await MapToEventAsync(entry, cancellationToken));
            }
        }
        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A message is resolved and decrypted only when its type, after upcasting, is relevant — or when the type does
    /// not resolve but carries the name of a relevant type, so that it fails as an unregistered type does. See
    /// <see cref="EventRelevance"/>.
    /// </remarks>
    public async Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventMessage> messages, EventRelevance relevance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(relevance);
        var result = new List<IEvent>();
        foreach (var message in messages)
        {
            if (Reads(message.EventTypeName, message.DataJson, relevance))
            {
                result.Add(await MapToEventEnvelopesAsync(message, cancellationToken));
            }
        }
        return result;
    }

    private bool Reads(string recordedName, string dataJson, EventRelevance relevance)
    {
        var effectiveName = EffectiveNameOf(recordedName, dataJson);
        if (typeResolver.TryResolve(effectiveName, out var eventType) && eventType is not null)
        {
            return relevance.Includes(eventType);
        }

        if (relevance.MayName(SimpleNameOf(recordedName)) || relevance.MayName(SimpleNameOf(effectiveName)))
        {
            return true;
        }

        if (relevance.IsAnyResolvable
            && _skippedUnresolvable.Count < MaxRememberedNames
            && _skippedUnresolvable.TryAdd(effectiveName, 0))
        {
            LogUnresolvableEventSkipped(logger ?? NullLogger<EventMapperFactory>.Instance, effectiveName);
        }

        return false;
    }

    /// <summary>
    /// The type name after upcasting. The framework's pipeline chains upcasters by type name alone, so its answer is
    /// remembered per recorded name, up to a bound; a pipeline of the host's own may decide from the payload, so it is
    /// asked for every entry.
    /// </summary>
    private string EffectiveNameOf(string recordedName, string dataJson)
    {
        if (!_upcastsByNameOnly)
        {
            return upcasterPipeline.Upcast(recordedName, dataJson).EventTypeName;
        }

        if (_effectiveNames.TryGetValue(recordedName, out var known))
        {
            return known;
        }

        var effectiveName = upcasterPipeline.Upcast(recordedName, dataJson).EventTypeName;
        if (_effectiveNames.Count < MaxRememberedNames)
        {
            _effectiveNames.TryAdd(recordedName, effectiveName);
        }

        return effectiveName;
    }

    private static string SimpleNameOf(string recordedName)
    {
        var separator = recordedName.IndexOf(',');
        var typeName = (separator < 0 ? recordedName : recordedName[..separator]).Trim();
        var bracket = typeName.IndexOf('[');
        if (bracket >= 0)
        {
            typeName = typeName[..bracket];
        }

        var lastSeparator = typeName.LastIndexOfAny(['.', '+']);
        return lastSeparator < 0 ? typeName : typeName[(lastSeparator + 1)..];
    }

    [LoggerMessage(
        EventId = LogEvents.EventStore.UnresolvableEventSkippedForAnyResolvable,
        Level = LogLevel.Warning,
        Message = "Skipped events of type {EventTypeName}, which does not resolve in this host, for a reader that takes any resolvable event. Register the type or add an upcaster if the reader should see them; register it with AddTrustedType to acknowledge the skip. Logged once per host and event type.")]
    private static partial void LogUnresolvableEventSkipped(ILogger logger, string eventTypeName);

    private async Task<IEvent> MapToEventAsync(EventStreamEntry entry, CancellationToken cancellationToken)
    {
        var upcasted = upcasterPipeline.Upcast(entry.EventTypeName, entry.DataJson);
        var eventType = typeResolver.Resolve(upcasted.EventTypeName);
        var data = await serializer.DeserializeAsync(upcasted.DataJson, eventType, entry.TenantId, entry.UserId, cancellationToken) ??
                   throw new InvalidOperationException("Event data could not be deserialized.");

        var factory = s_factoryCache.GetOrAdd(eventType, static type => CreateEventFactory(type));
        return factory(entry.Id, entry.Version, data, entry.StreamId, entry.TenantId, entry.ActorUserId, entry.AggregateTypeName);
    }

    private async Task<IEvent> MapToEventEnvelopesAsync(EventMessage message, CancellationToken cancellationToken)
    {
        var upcasted = upcasterPipeline.Upcast(message.EventTypeName, message.DataJson);
        var eventType = typeResolver.Resolve(upcasted.EventTypeName);
        var data = await serializer.DeserializeAsync(upcasted.DataJson, eventType, message.TenantId, message.UserId, cancellationToken) ??
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
