using Stratara.Abstractions.EventSourcing;

namespace Stratara.Sagas.Abstractions;

/// <summary>
/// Fans an incoming event bundle out to every registered <see cref="ISaga"/> in parallel and runs the
/// per-saga dispatch via <see cref="ISagaHandler"/>.
/// </summary>
public interface ISagaManager
{
    /// <summary>Dispatch the given events to every registered saga. Sagas that have no relevant handler are skipped.</summary>
    /// <param name="events">
    /// The bundle's events. The framework's worker hands the framework's manager only the events a registered saga
    /// declares a handler for, leaving the others unread; a manager registered in its place receives every event of the
    /// bundle.
    /// </param>
    /// <param name="cancellationToken">Cancellation token propagated to all saga invocations.</param>
    Task HandleAsync(IReadOnlyList<IEvent> events, CancellationToken cancellationToken);
}
