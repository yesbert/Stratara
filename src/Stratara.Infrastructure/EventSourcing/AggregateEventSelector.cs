using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Diagnostics;
using Stratara.Shared.EventSourcing.Mapping;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Decides which recorded entries an aggregate reads when it is rebuilt, so an event the aggregate
/// declares no <c>Apply</c> for is skipped before its type is resolved or its payload decrypted.
/// </summary>
/// <remarks>
/// <para>
/// An entry whose type resolves, after upcasting, is read exactly when the dispatcher would bind an
/// <c>Apply</c> to it: the selector asks <see cref="Type.GetMethod(string, Type[])"/> the question
/// <see cref="EventStream"/> asks, for the payload type and for <see cref="IEvent{TEvent}"/> of it.
/// </para>
/// <para>
/// An entry whose type does not resolve cannot be asked about, so it is read — and fails in the mapper
/// as an unregistered type does — when it could still be one the aggregate applies: when its type name,
/// without namespace or assembly, is the name of a type an <c>Apply</c> takes, or when an <c>Apply</c>
/// takes an interface, an abstract class, <see cref="object"/> or a generic type, or is itself generic.
/// Every other unresolvable entry is skipped, and the skip is logged once per aggregate type and
/// recorded name.
/// </para>
/// <para>
/// The upcasters chain by type name, so every decision depends on the recorded name alone and is made
/// once per recorded name and aggregate type. Selection applies only while the registered
/// <see cref="IEventMapperFactory"/> is the framework's; a replaced mapper may resolve names this
/// selector cannot, so every entry is read.
/// </para>
/// </remarks>
internal sealed partial class AggregateEventSelector(
    ITrustedTypeResolver typeResolver,
    IEventUpcasterPipeline upcasterPipeline,
    IEventMapperFactory eventMapperFactory,
    ILogger<AggregateEventSelector>? logger = null)
{
    private const string ApplyMethodName = "Apply";
    private readonly bool _selects = eventMapperFactory is EventMapperFactory;
    private readonly ConcurrentDictionary<Type, HandlerProfile> _profiles = new();

    /// <summary>Returns the entries the aggregate reads, in their original order.</summary>
    /// <param name="aggregateType">The aggregate type being rebuilt.</param>
    /// <param name="entries">The recorded entries of its stream.</param>
    /// <returns><paramref name="entries"/> itself when every entry is read; otherwise the entries that are.</returns>
    public IReadOnlyList<EventStreamEntry> Select(Type aggregateType, IReadOnlyList<EventStreamEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(aggregateType);
        ArgumentNullException.ThrowIfNull(entries);

        if (!_selects || entries.Count == 0)
        {
            return entries;
        }

        var profile = _profiles.GetOrAdd(aggregateType, static type => HandlerProfile.Of(type));
        List<EventStreamEntry>? selected = null;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (Reads(aggregateType, profile, entry))
            {
                selected?.Add(entry);
                continue;
            }

            selected ??= [.. entries.Take(i)];
        }

        return selected ?? entries;
    }

    private bool Reads(Type aggregateType, HandlerProfile profile, EventStreamEntry entry)
    {
        if (profile.TryGetDecision(entry.EventTypeName, out var known))
        {
            return known;
        }

        var reads = Decide(aggregateType, profile, entry);
        profile.RememberDecision(entry.EventTypeName, reads);
        return reads;
    }

    private bool Decide(Type aggregateType, HandlerProfile profile, EventStreamEntry entry)
    {
        if (typeResolver.TryResolve(entry.EventTypeName, out var recordedType) && recordedType is not null && profile.Binds(recordedType))
        {
            return true;
        }

        var effectiveName = upcasterPipeline.Upcast(entry.EventTypeName, entry.DataJson).EventTypeName;
        if (typeResolver.TryResolve(effectiveName, out var effectiveType) && effectiveType is not null)
        {
            return profile.Binds(effectiveType);
        }

        if (profile.IsOpen || profile.MayName(entry.EventTypeName) || profile.MayName(effectiveName))
        {
            return true;
        }

        LogUnresolvableEventSkipped(logger ?? NullLogger<AggregateEventSelector>.Instance, effectiveName, aggregateType.FullName ?? aggregateType.Name);
        return false;
    }

    [LoggerMessage(
        EventId = LogEvents.EventStore.UnresolvableEventSkipped,
        Level = LogLevel.Warning,
        Message = "Rebuilding {AggregateType} skipped events of type {EventTypeName}, which does not resolve in this host and has the name of no type an Apply of the aggregate takes. If the aggregate should apply them, register the type or add an upcaster; to acknowledge the skip, register the type with AddTrustedType. Logged once per aggregate type and event type.")]
    private static partial void LogUnresolvableEventSkipped(ILogger logger, string eventTypeName, string aggregateType);

    private sealed class HandlerProfile
    {
        private readonly Type _aggregateType;
        private readonly HashSet<string> _handledNames;
        private readonly ConcurrentDictionary<Type, bool> _bindings = new();
        private readonly ConcurrentDictionary<string, bool> _decisions = new(StringComparer.Ordinal);

        private HandlerProfile(Type aggregateType, HashSet<string> handledNames, bool isOpen)
        {
            _aggregateType = aggregateType;
            _handledNames = handledNames;
            IsOpen = isOpen;
        }

        public bool IsOpen { get; }

        public bool TryGetDecision(string recordedName, out bool reads) => _decisions.TryGetValue(recordedName, out reads);

        public void RememberDecision(string recordedName, bool reads) => _decisions.TryAdd(recordedName, reads);

        public bool Binds(Type eventType) => _bindings.GetOrAdd(eventType, static (type, aggregate) => BindsApply(aggregate, type), _aggregateType);

        public bool MayName(string recordedName)
        {
            var typeName = TypeNameOf(recordedName);
            if (typeName.Contains('[', StringComparison.Ordinal))
            {
                return true;
            }

            var separator = typeName.LastIndexOfAny(['.', '+']);
            return _handledNames.Contains(separator < 0 ? typeName : typeName[(separator + 1)..]);
        }

        public static HandlerProfile Of(Type aggregateType)
        {
            var handledNames = new HashSet<string>(StringComparer.Ordinal);
            var isOpen = false;
            foreach (var method in aggregateType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.Name != ApplyMethodName)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                {
                    continue;
                }

                var handledType = PayloadTypeOf(parameters[0].ParameterType);
                isOpen |= method.IsGenericMethodDefinition || TakesOtherTypes(handledType);
                handledNames.Add(handledType.Name);
            }

            return new HandlerProfile(aggregateType, handledNames, isOpen);
        }

        private static bool BindsApply(Type aggregateType, Type eventType)
        {
            try
            {
                return aggregateType.GetMethod(ApplyMethodName, [eventType]) is not null
                       || aggregateType.GetMethod(ApplyMethodName, [typeof(IEvent<>).MakeGenericType(eventType)]) is not null;
            }
            catch (AmbiguousMatchException)
            {
                return true;
            }
        }

        private static Type PayloadTypeOf(Type parameterType) =>
            parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(IEvent<>)
                ? parameterType.GetGenericArguments()[0]
                : parameterType;

        private static bool TakesOtherTypes(Type handledType) =>
            handledType.IsInterface
            || handledType.IsAbstract
            || handledType.IsGenericType
            || handledType.IsGenericParameter
            || handledType == typeof(object);

        private static string TypeNameOf(string recordedName)
        {
            var separator = recordedName.IndexOf(',');
            return (separator < 0 ? recordedName : recordedName[..separator]).Trim();
        }
    }
}
