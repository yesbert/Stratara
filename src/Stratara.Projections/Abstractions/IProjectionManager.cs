using Stratara.Abstractions.EventSourcing;

namespace Stratara.Projections.Abstractions;

/// <summary>
/// Fans an incoming event bundle out to every registered <see cref="IProjection"/> in parallel and runs the
/// per-projection dispatch via <see cref="IProjectionHandler"/>.
/// </summary>
public interface IProjectionManager
{
    /// <summary>Dispatch the given events to every registered projection. Projections that have no relevant handler are skipped.</summary>
    /// <param name="events">The full event-bundle contents.</param>
    /// <param name="cancellationToken">Cancellation token propagated to all projection invocations.</param>
    Task HandleAsync(IReadOnlyList<IEvent> events, CancellationToken cancellationToken);
}
