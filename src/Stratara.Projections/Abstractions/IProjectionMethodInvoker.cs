using Stratara.Abstractions.EventSourcing;

namespace Stratara.Projections.Abstractions;

/// <summary>
/// Reflection-based discovery + invocation helper that locates a projection's <c>HandleAsync</c> methods
/// and caches compiled delegates for hot-path dispatch.
/// </summary>
/// <remarks>
/// Implementations are expected to use process-lifetime caches keyed by <c>(projectionType, eventType)</c>;
/// the cache plateaus once every registered projection has been dispatched against every event type it
/// declares a handler for.
/// </remarks>
public interface IProjectionMethodInvoker
{
    /// <summary>Returns <c>true</c> if the supplied delegate is the cached no-op (i.e. the projection declares no handler for that event type).</summary>
    /// <param name="delegateOperation">The delegate to test.</param>
    bool IsNoOp(Func<IProjection, object, CancellationToken, Task> delegateOperation);

    /// <summary>Returns the distinct event types the projection declares <c>HandleAsync</c> overloads for, computing them on first call and caching thereafter.</summary>
    /// <param name="projection">The projection to inspect.</param>
    Type[] GetOrCreateRelevantEventTypes(IProjection projection);

    /// <summary>Returns a compiled delegate that invokes the projection's <c>HandleAsync(eventType, CancellationToken)</c> overload, or the cached no-op if the projection declares no handler for the type.</summary>
    /// <param name="projection">The projection that owns the handler.</param>
    /// <param name="eventType">The event payload type (or wrapping <see cref="IEvent{T}"/> type) to look up.</param>
    Func<IProjection, object, CancellationToken, Task> GetOrCreateDelegate(IProjection projection, Type eventType);
}
