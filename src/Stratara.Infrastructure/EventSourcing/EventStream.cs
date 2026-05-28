using System.Collections.Concurrent;
using System.Linq.Expressions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Reflections;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Replay-helper that builds aggregates from event lists by invoking their <c>Apply</c> methods.
/// Compiled <see cref="Expression"/> delegates are cached per <c>(aggregateType, eventType)</c> pair
/// so reflection only happens once per pairing.
/// </summary>
/// <remarks>
/// The dispatcher first tries an <c>Apply(TData)</c> method matching the runtime
/// <see cref="IEvent.Data"/> type; if none exists, it falls back to <c>Apply(IEvent&lt;TData&gt;)</c>.
/// Missing <c>Apply</c> overloads are silently ignored so aggregates can opt in to specific event types.
/// </remarks>
public static class EventStream
{
    private const string ApplyMethodName = "Apply";
    private static readonly Action<object, object> NoOp = (_, _) => { };
    private static readonly ConcurrentDictionary<(Type AggregateType, Type EventType), Action<object, object>> ApplyDelegatesCache = new();

    /// <summary>Builds a new aggregate of type <typeparamref name="TAggregate"/> and applies the given events.</summary>
    /// <typeparam name="TAggregate">Aggregate type with a public parameterless constructor.</typeparam>
    /// <param name="events">Events to replay onto the freshly-created aggregate.</param>
    /// <returns>The fully-applied aggregate instance.</returns>
    public static TAggregate Aggregate<TAggregate>(IReadOnlyList<IEvent> events)
        where TAggregate : notnull, new()
    {
        ArgumentNullException.ThrowIfNull(events);

        var aggregate = new TAggregate();
        aggregate.ApplyEvents(events);
        return aggregate;
    }

    /// <summary>Builds a new aggregate of the given runtime type and applies the events.</summary>
    /// <param name="aggregateType">Concrete aggregate type (must be activatable via <see cref="ObjectFactory"/>).</param>
    /// <param name="events">Events to replay onto the new instance.</param>
    /// <returns>The fully-applied aggregate instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when an instance of <paramref name="aggregateType"/> cannot be created.</exception>
    public static object Aggregate(Type aggregateType, IReadOnlyList<IEvent> events)
    {
        ArgumentNullException.ThrowIfNull(aggregateType);
        ArgumentNullException.ThrowIfNull(events);

        var aggregate = ObjectFactory.CreateInstance(aggregateType)
                        ?? throw new InvalidOperationException($"Could not create instance of {aggregateType.FullName}");

        aggregate.ApplyEvents(events);
        return aggregate;
    }

    /// <summary>Applies a sequence of events to an existing aggregate instance in order.</summary>
    /// <param name="aggregate">Target aggregate instance.</param>
    /// <param name="events">Events to replay in declaration order.</param>
    public static void ApplyEvents(this object aggregate, IReadOnlyList<IEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(aggregate);
        foreach (var @event in events)
        {
            aggregate.ApplyEvent(@event);
        }
    }

    private static void ApplyEvent(this object aggregate, IEvent @event)
    {
        var aggregateType = aggregate.GetType();

        if (TryApplyEventData(@event, aggregate, aggregateType))
        {
            return;
        }

        TryApplyWrappedEvent(@event, aggregate, aggregateType);
    }

    private static bool TryApplyEventData(IEvent @event, object aggregate, Type aggregateType)
    {
        var eventDataType = @event.Data.GetType();
        var applyDelegate = GetOrCreateDelegate(aggregateType, eventDataType);

        if (applyDelegate == NoOp)
        {
            return false;
        }

        applyDelegate(aggregate, @event.Data);
        return true;
    }

    private static void TryApplyWrappedEvent(IEvent @event, object aggregate, Type aggregateType)
    {
        var eventDataType = @event.Data.GetType();
        var wrappedEventType = typeof(IEvent<>).MakeGenericType(eventDataType);
        var applyDelegate = GetOrCreateDelegate(aggregateType, wrappedEventType);

        if (applyDelegate != NoOp)
        {
            applyDelegate(aggregate, @event);
        }
    }

    private static Action<object, object> GetOrCreateDelegate(Type aggregateType, Type eventType)
    {
        var key = (aggregateType, eventType);
        return ApplyDelegatesCache.GetOrAdd(key, static k => CreateApplyDelegate(k.AggregateType, k.EventType));
    }

    private static Action<object, object> CreateApplyDelegate(Type aggregateType, Type eventType)
    {
        var method = aggregateType.GetMethod(ApplyMethodName, [eventType]);
        if (method is null)
        {
            return NoOp;
        }

        var aggregateParam = Expression.Parameter(typeof(object), "aggregate");
        var eventParam = Expression.Parameter(typeof(object), "event");

        var castAgg = Expression.Convert(aggregateParam, aggregateType);
        var castEvt = Expression.Convert(eventParam, eventType);

        var call = Expression.Call(castAgg, method, castEvt);

        return Expression.Lambda<Action<object, object>>(call, aggregateParam, eventParam).Compile();
    }
}
