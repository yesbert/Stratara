using System.Collections.Concurrent;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Decides from a commit's event type names whether it concerns a store reader, as the reader decides it when it maps
/// the entries: an event concerns the reader when its type, after upcasting, is one the reader's consumer applies. A
/// commit's wake-up carries the names only, not the payloads, so the name is upcast through the host's own pipeline
/// with an empty payload — the framework's pipeline chains upcasters by name, so the name it ends at does not depend on
/// the payload. A name whose type the silo cannot tell that way is taken to concern every reader: one the type
/// resolver does not know, one whose upcaster refuses the empty payload, any name where the host's upcaster pipeline
/// is not the framework's, which may decide from the payload, or where the composition registers no type resolver or
/// pipeline at all. A reader woken in vain costs one read; one left asleep would wait for its poll.
/// </summary>
internal sealed class CommitRelevance(ITrustedTypeResolver? typeResolver = null, IEventUpcasterPipeline? upcasterPipeline = null)
{
    private const int MaxRememberedNames = 4096;
    private const string EmptyPayload = "{}";

    private readonly ConcurrentDictionary<string, Type?> _types = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Whether a commit of <paramref name="eventTypeNames"/> concerns a reader whose consumer's relevance
    /// <paramref name="relevanceOf"/> answers: every reader where there are no names — a wake-up of an earlier version,
    /// a restart — or no <paramref name="relevance"/>, and every reader whose relevance cannot be worked out, so that a
    /// failure there never costs the other readers of the commit their wake-up.
    /// </summary>
    public static bool Concerns(CommitRelevance? relevance, Func<EventRelevance?> relevanceOf, IReadOnlyCollection<string>? eventTypeNames)
    {
        if (eventTypeNames is null || relevance is null)
        {
            return true;
        }

        try
        {
            return relevance.Concerns(relevanceOf(), eventTypeNames);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return true;
        }
    }

    /// <summary>
    /// The type an event of <paramref name="recordedName"/> maps to, or <see langword="null"/> where the name alone does
    /// not tell. The answer is remembered per name, up to a bound.
    /// </summary>
    private Type? TypeOf(string recordedName)
    {
        if (typeResolver is null || upcasterPipeline is not EventUpcasterPipeline pipeline)
        {
            return null;
        }

        if (_types.TryGetValue(recordedName, out var known))
        {
            return known;
        }

        var type = Resolve(typeResolver, pipeline, recordedName);
        if (_types.Count < MaxRememberedNames)
        {
            _types.TryAdd(recordedName, type);
        }

        return type;
    }

    private static Type? Resolve(ITrustedTypeResolver typeResolver, EventUpcasterPipeline pipeline, string recordedName)
    {
        string effectiveName;
        try
        {
            effectiveName = pipeline.Upcast(recordedName, EmptyPayload).EventTypeName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }

        return typeResolver.TryResolve(effectiveName, out var resolved) ? resolved : null;
    }
}
