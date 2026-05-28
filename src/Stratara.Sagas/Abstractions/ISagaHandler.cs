using Stratara.Abstractions.EventSourcing;

namespace Stratara.Sagas.Abstractions;

/// <summary>
/// Dispatcher that drives a single <see cref="ISaga"/> through a list of events, using the
/// <see cref="ISagaMethodInvoker"/> to look up cached handler delegates.
/// </summary>
public interface ISagaHandler
{
    /// <summary>Replays a list of events against the given saga instance, in order.</summary>
    /// <param name="saga">The saga instance to drive.</param>
    /// <param name="events">Events to dispatch (already filtered to relevant types by the manager).</param>
    /// <param name="cancellationToken">Cancellation token propagated to each handler invocation.</param>
    Task HandleAsync(ISaga saga, IReadOnlyList<IEvent> events, CancellationToken cancellationToken = default);

    /// <summary>Returns the event types the saga declares <c>HandleAsync</c> methods for.</summary>
    /// <param name="saga">The saga to inspect.</param>
    Type[] GetRelevantEventTypes(ISaga saga);

    /// <summary>Returns the assembly-qualified names of the saga's relevant event types — used for wire-level matching.</summary>
    /// <param name="saga">The saga to inspect.</param>
    string[] GetRelevantEventTypeNames(ISaga saga);

    /// <summary>Returns a stable display name for the saga (used in diagnostics + logging scopes).</summary>
    /// <param name="saga">The saga to inspect.</param>
    string GetSagaName(ISaga saga);
}
