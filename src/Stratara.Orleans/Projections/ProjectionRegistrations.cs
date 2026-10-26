using Microsoft.Extensions.DependencyInjection;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The names of the projections the silo registers, as the projection handler gives them — the names its store readers
/// are started and woken under — worked out once, the first time they are asked for, rather than by constructing every
/// projection on each reader's activation. Readers activated together wait for the one working them out; a failure to
/// work them out is not kept, so the next question tries again.
/// </summary>
internal sealed class ProjectionRegistrations(IServiceScopeFactory scopeFactory)
{
    private readonly Lock _gate = new();
    private HashSet<string>? _names;

    /// <summary>Whether a projection named <paramref name="projectionName"/> is registered on this silo.</summary>
    public bool Contains(string projectionName) => (Volatile.Read(ref _names) ?? Read()).Contains(projectionName);

    private HashSet<string> Read()
    {
        lock (_gate)
        {
            if (_names is { } names)
            {
                return names;
            }

            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IProjectionHandler>();
            names = scope.ServiceProvider.GetServices<IProjection>().Select(handler.GetProjectionName).ToHashSet(StringComparer.Ordinal);
            Volatile.Write(ref _names, names);
            return names;
        }
    }
}
