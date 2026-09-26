using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Sagas.Abstractions;

namespace Stratara.Sagas.Services;

/// <summary>
/// The events a host's sagas have a use for: the union of the types each registered saga declares a handler for. The
/// mapper leaves every other event unread.
/// </summary>
internal static class SagaEventRelevance
{
    /// <summary>The relevance of the sagas registered in <paramref name="services"/>.</summary>
    /// <param name="services">A scope's service provider.</param>
    /// <returns>The union of the sagas' relevant types; none where no saga handler is registered.</returns>
    public static EventRelevance Of(IServiceProvider services)
    {
        if (services.GetService<ISagaHandler>() is not { } handler)
        {
            return EventRelevance.ForTypes([]);
        }

        return EventRelevance.ForTypes(services.GetServices<ISaga>().SelectMany(handler.GetRelevantEventTypes));
    }
}
