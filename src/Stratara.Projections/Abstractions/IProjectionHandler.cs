using Stratara.Abstractions.EventSourcing;

namespace Stratara.Projections.Abstractions;

/// <summary>
/// Drives a single <see cref="IProjection"/> through a list of events, using the
/// <see cref="IProjectionMethodInvoker"/> to look up cached handler delegates.
/// </summary>
public interface IProjectionHandler
{
    /// <summary>Projects a list of events onto the given projection, in order.</summary>
    /// <param name="projection">The projection instance to drive.</param>
    /// <param name="events">Events to dispatch (already filtered to relevant types by the manager).</param>
    /// <param name="cancellationToken">Cancellation token propagated to each handler invocation.</param>
    Task ProjectAsync(IProjection projection, IReadOnlyList<IEvent> events, CancellationToken cancellationToken = default);

    /// <summary>Returns the event types the projection declares <c>HandleAsync</c> methods for.</summary>
    /// <param name="projection">The projection to inspect.</param>
    Type[] GetRelevantEventTypes(IProjection projection);

    /// <summary>Returns the assembly-qualified names of the projection's relevant event types — used for wire-level matching.</summary>
    /// <param name="projection">The projection to inspect.</param>
    string[] GetRelevantEventTypeNames(IProjection projection);

    /// <summary>Returns a stable display name for the projection (used in diagnostics + logging scopes).</summary>
    /// <param name="projection">The projection to inspect.</param>
    string GetProjectionName(IProjection projection);
}
