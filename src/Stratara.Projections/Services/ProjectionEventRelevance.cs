using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Projections.Abstractions;

namespace Stratara.Projections.Services;

/// <summary>
/// The events a host's projections have a use for: the union of the types each registered projection is dispatched,
/// deletion facts included for one that forgets deleted tenants. The mapper leaves every other event unread.
/// </summary>
internal static class ProjectionEventRelevance
{
    /// <summary>The relevance of the projections registered in <paramref name="services"/>.</summary>
    /// <param name="services">A scope's service provider.</param>
    /// <returns>The union of the projections' relevant types; none where no projection handler is registered.</returns>
    public static EventRelevance Of(IServiceProvider services)
    {
        if (services.GetService<IProjectionHandler>() is not { } handler)
        {
            return EventRelevance.ForTypes([]);
        }

        return EventRelevance.ForTypes(services.GetServices<IProjection>().SelectMany(handler.GetRelevantEventTypes));
    }
}
