using Microsoft.Extensions.DependencyInjection;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The names of the projections the silo registers, as the projection handler gives them — the names its store readers
/// are started and woken under — worked out once, the first time they are asked for, rather than by constructing every
/// projection on each reader's activation. A failure to work them out is not kept, so the next question tries again.
/// </summary>
internal sealed class ProjectionRegistrations(IServiceScopeFactory scopeFactory)
{
    private readonly Lazy<HashSet<string>> _names = new(() => Read(scopeFactory), LazyThreadSafetyMode.PublicationOnly);

    /// <summary>Whether a projection named <paramref name="projectionName"/> is registered on this silo.</summary>
    public bool Contains(string projectionName) => _names.Value.Contains(projectionName);

    private static HashSet<string> Read(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IProjectionHandler>();
        return scope.ServiceProvider.GetServices<IProjection>().Select(handler.GetProjectionName).ToHashSet(StringComparer.Ordinal);
    }
}
