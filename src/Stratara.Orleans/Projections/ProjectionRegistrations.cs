using Microsoft.Extensions.DependencyInjection;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The names of the projections the silo registers, as the projection handler gives them — the names its store readers
/// are started, woken, waited for, reset and rebuilt under — worked out once, the first time they are asked for, rather
/// than by constructing every projection wherever a name is needed. Readers activated together wait for the one working
/// them out; a failure to work them out is not kept, so the next question tries again.
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
            foreach (var projection in scope.ServiceProvider.GetServices<IProjection>())
            {
                var name = handler.GetProjectionName(projection);
                if (byName.Add(name))
                {
                    names.Add(name);
                }

                byType.TryAdd(projection.GetType(), name);
            }

            snapshot = new Snapshot(names, byName, byType);
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
    }

    private sealed record Snapshot(IReadOnlyList<string> Names, HashSet<string> ByName, Dictionary<Type, string> ByType);
}
