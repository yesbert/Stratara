using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Projections.Abstractions;

/// <summary>
/// Dispatches a single event to all registered projections. Implementations are expected to route the event
/// through the configured <see cref="IProjectionManager"/> pipeline.
/// </summary>
public interface IProjectionDispatcher
{
    /// <summary>Dispatches the supplied event to every relevant projection.</summary>
    /// <param name="wrappedEvent">The event (already wrapped in <see cref="IEvent"/>) to dispatch.</param>
    /// <param name="cancellationToken">Cancellation token propagated to each projection invocation.</param>
    Task DispatchAsync(IEvent wrappedEvent, CancellationToken cancellationToken);
}
