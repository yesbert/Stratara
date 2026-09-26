using System.Collections.Concurrent;
using System.Reflection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Decides which recorded entries an aggregate reads when it is rebuilt, so an event the aggregate
/// declares no <c>Apply</c> for is skipped before it is resolved, deserialized or decrypted.
/// </summary>
/// <remarks>
/// <para>
/// The handler profile of each aggregate type is built once and cached. It holds the payload types
/// the aggregate's public <c>Apply</c> methods take, with <see cref="IEvent{TEvent}"/> unwrapped to its
/// payload. An entry is read when its recorded type resolves to a handled type, when its type after
/// upcasting does, or when that type does not resolve but carries the full name of a handled type,
/// so a moved or unregistered handled type still fails in the mapper rather than being skipped.
/// </para>
/// <para>
/// Where the profile cannot rule out that the dispatcher binds an event of some other type, it reads
/// every entry: a handler taking an unsealed class, an interface, a primitive, an array or a generic
/// type, a generic <c>Apply</c>, or a handled type the trusted-type resolver does not resolve to itself.
/// Registrations are only ever added, so a cached profile never becomes wrong.
/// </para>
/// </remarks>
internal sealed class AggregateEventSelector(ITrustedTypeResolver typeResolver, IEventUpcasterPipeline upcasterPipeline)
{
    private const string ApplyMethodName = "Apply";
    private readonly ConcurrentDictionary<Type, HandlerProfile> _profiles = new();

    /// <summary>Returns the entries the aggregate reads, in their original order.</summary>
    /// <param name="aggregateType">The aggregate type being rebuilt.</param>
    /// <param name="entries">The recorded entries of its stream.</param>
    /// <returns><paramref name="entries"/> itself when every entry is read; otherwise the entries that are.</returns>
    public IReadOnlyList<EventStreamEntry> Select(Type aggregateType, IReadOnlyList<EventStreamEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(aggregateType);
        ArgumentNullException.ThrowIfNull(entries);

        var profile = _profiles.GetOrAdd(aggregateType, static (type, resolver) => HandlerProfile.Of(type, resolver), typeResolver);
        if (profile.ReadsEverything)
        {
            return entries;
        }

        List<EventStreamEntry>? selected = null;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (Reads(profile, entry))
            {
                selected?.Add(entry);
                continue;
            }

            selected ??= [.. entries.Take(i)];
        }

        return selected ?? entries;
    }

    private bool Reads(HandlerProfile profile, EventStreamEntry entry)
    {
        if (typeResolver.TryResolve(entry.EventTypeName, out var recordedType) && recordedType is not null && profile.Handles(recordedType))
        {
            return true;
        }

        var effectiveName = upcasterPipeline.Upcast(entry.EventTypeName, entry.DataJson).EventTypeName;
        if (typeResolver.TryResolve(effectiveName, out var effectiveType))
        {
            return effectiveType is not null && profile.Handles(effectiveType);
        }

        return profile.HandlesTypeNamed(TypeNameOf(effectiveName));
    }

    private static string TypeNameOf(string recordedName)
    {
        var separator = recordedName.IndexOf(',');
        return (separator < 0 ? recordedName : recordedName[..separator]).Trim();
    }

    private sealed class HandlerProfile
    {
        private static readonly HandlerProfile Everything = new([], readsEverything: true);

        private readonly HashSet<Type> _types;
        private readonly HashSet<string> _typeNames;

        private HandlerProfile(HashSet<Type> types, bool readsEverything)
        {
            _types = types;
            _typeNames = new HashSet<string>(types.Select(t => t.FullName ?? t.Name), StringComparer.Ordinal);
            ReadsEverything = readsEverything;
        }

        public bool ReadsEverything { get; }

        public bool Handles(Type type) => _types.Contains(type);

        public bool HandlesTypeNamed(string fullName) => _typeNames.Contains(fullName);

        public static HandlerProfile Of(Type aggregateType, ITrustedTypeResolver resolver)
        {
            var types = new HashSet<Type>();
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
                if (method.IsGenericMethodDefinition || !BindsOnlyItself(handledType) || !IsRegistered(handledType, resolver))
                {
                    return Everything;
                }

                types.Add(handledType);
            }

            return new HandlerProfile(types, readsEverything: false);
        }

        private static Type PayloadTypeOf(Type parameterType) =>
            parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(IEvent<>)
                ? parameterType.GetGenericArguments()[0]
                : parameterType;

        private static bool BindsOnlyItself(Type type) =>
            type is { IsGenericType: false, IsGenericParameter: false, IsByRef: false, IsPointer: false, IsArray: false, IsPrimitive: false }
            && (type.IsValueType || type is { IsClass: true, IsSealed: true });

        private static bool IsRegistered(Type type, ITrustedTypeResolver resolver) =>
            type.AssemblyQualifiedName is { } name
            && resolver.TryResolve(name, out var resolved)
            && resolved == type;
    }
}
