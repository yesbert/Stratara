namespace Stratara.Sagas.Abstractions;

/// <summary>
/// Reflection-cache facade that discovers <c>HandleAsync</c> methods on a saga and creates compiled
/// delegates the dispatch can call. Implementations are expected to memoize lookups for process lifetime.
/// </summary>
public interface ISagaMethodInvoker
{
    /// <summary>
    /// Returns <c>true</c> if the given delegate is the cached no-op marker, i.e. the saga has no
    /// handler for the event type that produced it.
    /// </summary>
    /// <param name="delegateOperation">A delegate returned by <see cref="GetOrCreateDelegate"/>.</param>
    bool IsNoOp(Func<ISaga, object, CancellationToken, Task> delegateOperation);

    /// <summary>Returns the set of event types the saga declares a <c>HandleAsync</c> method for, caching the result.</summary>
    /// <param name="saga">The saga to inspect.</param>
    Type[] GetOrCreateRelevantEventTypes(ISaga saga);

    /// <summary>
    /// Returns (or creates and caches) the compiled delegate that invokes <c>HandleAsync</c> on the saga
    /// for the given event type. Returns a no-op delegate if no matching method exists.
    /// </summary>
    /// <param name="saga">The saga to dispatch on.</param>
    /// <param name="eventType">The runtime event type to look up.</param>
    Func<ISaga, object, CancellationToken, Task> GetOrCreateDelegate(ISaga saga, Type eventType);
}
