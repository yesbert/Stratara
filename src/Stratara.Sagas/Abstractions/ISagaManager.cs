using Stratara.Abstractions.EventSourcing;

namespace Stratara.Sagas.Abstractions;

/// <summary>
/// Fans an incoming event bundle out to every registered <see cref="ISaga"/> in parallel and runs the
/// per-saga dispatch via <see cref="ISagaHandler"/>.
/// </summary>
public interface ISagaManager
{
    /// <summary>Dispatch the given events to every registered saga. Sagas that have no relevant handler are skipped.</summary>
    /// <param name="events">The full event-bundle contents.</param>
    /// <param name="cancellationToken">Cancellation token propagated to all saga invocations.</param>
    Task HandleAsync(IReadOnlyList<IEvent> events, CancellationToken cancellationToken);
}
