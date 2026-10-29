using System.Collections.Concurrent;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Decides from a commit's event type names whether it concerns a store reader, as the reader decides it when it maps
/// the entries: an event concerns the reader when its type, after upcasting, is one the reader's consumer applies. A
/// commit's wake-up carries the names only, not the payloads, so a name whose type the silo cannot tell without the
/// payload is taken to concern every reader — one the type resolver does not know, one an upcaster is registered for,
/// or any name where the host's upcaster pipeline is not the framework's, which may decide from the payload, or where the
/// composition registers no type resolver or pipeline at all. A reader woken in vain costs one read; one left asleep
/// would wait for its poll.
/// </summary>
internal sealed class CommitRelevance
{
    private const int MaxRememberedNames = 4096;

    private readonly ITrustedTypeResolver? _typeResolver;
    private readonly bool _decidesByName;
    private readonly HashSet<string> _upcastNames;
    private readonly ConcurrentDictionary<string, Type?> _types = new(StringComparer.Ordinal);

    public CommitRelevance(ITrustedTypeResolver? typeResolver = null, IEventUpcasterPipeline? upcasterPipeline = null, IEnumerable<IEventUpcaster>? upcasters = null)
    {
        _typeResolver = typeResolver;
        _decidesByName = typeResolver is not null && upcasterPipeline is EventUpcasterPipeline;
        _upcastNames = new HashSet<string>((upcasters ?? []).Select(upcaster => SimpleNameOf(upcaster.SourceEventTypeName)), StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a commit of <paramref name="eventTypeNames"/> concerns a reader whose consumer applies
    /// <paramref name="relevance"/>. A consumer whose relevance is unknown, or that takes any resolvable event, is
    /// concerned by every commit.
    /// </summary>
    public bool Concerns(EventRelevance? relevance, IReadOnlyCollection<string> eventTypeNames)
    {
        if (relevance is null || relevance.IsAnyResolvable)
        {
            return true;
        }

        foreach (var name in eventTypeNames)
        {
            if (TypeOf(name) is not { } type || relevance.Includes(type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The type an event of <paramref name="recordedName"/> maps to, or <see langword="null"/> where the name alone does not tell.</summary>
    private Type? TypeOf(string recordedName)
    {
        if (_typeResolver is null || !_decidesByName || _upcastNames.Contains(SimpleNameOf(recordedName)))
        {
            return null;
        }

        if (_types.TryGetValue(recordedName, out var known))
        {
            return known;
        }

        var type = _typeResolver.TryResolve(recordedName, out var resolved) ? resolved : null;
        if (_types.Count < MaxRememberedNames)
        {
            _types.TryAdd(recordedName, type);
        }

        return type;
    }

    /// <summary>The type's own name, without namespace, assembly or generic arguments: what two spellings of one type share.</summary>
    private static string SimpleNameOf(string typeName)
    {
        var separator = typeName.IndexOf(',');
        var name = (separator < 0 ? typeName : typeName[..separator]).Trim();
        var bracket = name.IndexOf('[');
        if (bracket >= 0)
        {
            name = name[..bracket];
        }

        var lastSeparator = name.LastIndexOfAny(['.', '+']);
        return lastSeparator < 0 ? name : name[(lastSeparator + 1)..];
    }
}
