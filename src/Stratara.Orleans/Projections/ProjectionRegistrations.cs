using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The names of the projections the silo registers, as the projection handler gives them — the names its store readers
/// are started, woken, waited for, reset and rebuilt under — worked out once, the first time they are asked for, rather
/// than by constructing every projection wherever a name is needed — and, with each name, the event types the projection
/// applies, which decide whether a commit wakes its readers. Readers activated together wait for the one working them
/// out; a failure to work them out is not kept, so the next question tries again.
/// </summary>
internal sealed class ProjectionRegistrations(IServiceScopeFactory scopeFactory)
{
    private readonly Lock _gate = new();
    private Snapshot? _snapshot;

    /// <summary>Every registered projection's name once, in registration order.</summary>
    public IReadOnlyList<string> Names => Current.Names;

    /// <summary>Whether a projection named <paramref name="projectionName"/> is registered on this silo.</summary>
    public bool Contains(string projectionName) => Current.ByName.Contains(projectionName);

    /// <summary>The name the projection of type <paramref name="projectionType"/> reads under, or <see langword="null"/> where no projection of that type is registered.</summary>
    public string? NameOf(Type projectionType) => Current.ByType.GetValueOrDefault(projectionType);

    /// <summary>
    /// The events the projection named <paramref name="projectionName"/> applies, as its reader decides them, or
    /// <see langword="null"/> where no projection of that name is registered.
    /// </summary>
    public EventRelevance? RelevanceOf(string projectionName) => Current.Relevance.GetValueOrDefault(projectionName);

    private Snapshot Current => Volatile.Read(ref _snapshot) ?? Read();

    private Snapshot Read()
    {
        lock (_gate)
        {
            if (_snapshot is { } snapshot)
            {
                return snapshot;
            }

            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IProjectionHandler>();
            var names = new List<string>();
            var byName = new HashSet<string>(StringComparer.Ordinal);
            var byType = new Dictionary<Type, string>();
            var relevance = new Dictionary<string, EventRelevance>(StringComparer.Ordinal);
            foreach (var projection in scope.ServiceProvider.GetServices<IProjection>())
            {
                var name = handler.GetProjectionName(projection);
                if (byName.Add(name))
                {
                    names.Add(name);
                    if (RelevanceOf(handler, projection) is { } applies)
                    {
                        relevance[name] = applies;
                    }
                }

                byType.TryAdd(projection.GetType(), name);
            }

            snapshot = new Snapshot(names, byName, byType, relevance);
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
    }

    /// <summary>
    /// The events <paramref name="projection"/> applies, or <see langword="null"/> where they cannot be worked out — then
    /// every commit wakes its readers, and its own reader reports the failure where it applies.
    /// </summary>
    private static EventRelevance? RelevanceOf(IProjectionHandler handler, IProjection projection)
    {
        try
        {
            return EventRelevance.ForTypes(handler.GetRelevantEventTypes(projection));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }
    }

    private sealed record Snapshot(IReadOnlyList<string> Names, HashSet<string> ByName, Dictionary<Type, string> ByType, Dictionary<string, EventRelevance> Relevance);
}
