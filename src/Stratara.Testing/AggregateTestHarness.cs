using System.Collections.Concurrent;
using System.Linq.Expressions;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Testing;

/// <summary>
/// A given/when/then harness that rehydrates an aggregate from a sequence of events using the same
/// reflection-based <c>Apply</c> dispatch as the production aggregation service — so a unit test
/// exercises the real apply logic without an event store, database, or session context.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type — must have a public parameterless constructor.</typeparam>
/// <remarks>
/// Each event is dispatched to <c>Apply(TEvent)</c> if present, otherwise to
/// <c>Apply(IEvent&lt;TEvent&gt;)</c> (mirroring the framework's two-phase dispatch). Unlike
/// production — which silently ignores an event with no matching <c>Apply</c> overload — the harness
/// throws by default to surface a forgotten or mistyped overload; opt back into the lenient
/// behavior with <see cref="IgnoringUnmappedEvents"/>.
/// </remarks>
/// <example>
/// <code>
/// var account = AggregateTestHarness&lt;Account&gt;
///     .Given(new AccountOpened(id, "Ada", 100m))
///     .And(new AmountWithdrawn(id, 30m))
///     .Build();
///
/// Assert.Equal(70m, account.Balance);
/// </code>
/// </example>
public sealed class AggregateTestHarness<TAggregate>
    where TAggregate : class, new()
{
    private readonly List<object> _events = [];
    private bool _ignoreUnmapped;

    private AggregateTestHarness()
    {
    }

    /// <summary>Start a harness with the given prior events.</summary>
    /// <param name="events">The events to apply, in order.</param>
    /// <returns>A harness seeded with <paramref name="events"/>.</returns>
    public static AggregateTestHarness<TAggregate> Given(params object[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var harness = new AggregateTestHarness<TAggregate>();
        harness._events.AddRange(events);
        return harness;
    }

    /// <summary>Start a harness with no prior events (rehydrates a freshly constructed aggregate).</summary>
    /// <returns>An empty harness.</returns>
    public static AggregateTestHarness<TAggregate> GivenNoEvents() => new();

    /// <summary>Append more events to apply after the ones already staged.</summary>
    /// <param name="events">The events to apply, in order.</param>
    /// <returns>The same harness for chaining.</returns>
    public AggregateTestHarness<TAggregate> And(params object[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events.AddRange(events);
        return this;
    }

    /// <summary>
    /// Match production's lenient dispatch: events with no matching <c>Apply</c> overload are skipped
    /// instead of throwing.
    /// </summary>
    /// <returns>The same harness for chaining.</returns>
    public AggregateTestHarness<TAggregate> IgnoringUnmappedEvents()
    {
        _ignoreUnmapped = true;
        return this;
    }

    /// <summary>Construct the aggregate and apply every staged event in order.</summary>
    /// <returns>The rehydrated aggregate.</returns>
    public TAggregate Build()
    {
        var aggregate = new TAggregate();
        var version = 0L;
        foreach (var @event in _events)
        {
            ArgumentNullException.ThrowIfNull(@event);
            AggregateEventApplier.Apply(aggregate, @event, ++version, _ignoreUnmapped);
        }

        return aggregate;
    }
}

/// <summary>One-line shortcuts for rehydrating an aggregate from events in tests.</summary>
public static class Aggregate
{
    /// <summary>Rehydrate <typeparamref name="TAggregate"/> from <paramref name="events"/>.</summary>
    /// <typeparam name="TAggregate">The aggregate type — must have a public parameterless constructor.</typeparam>
    /// <param name="events">The events to apply, in order.</param>
    /// <returns>The rehydrated aggregate.</returns>
    public static TAggregate Rehydrate<TAggregate>(params object[] events)
        where TAggregate : class, new() =>
        AggregateTestHarness<TAggregate>.Given(events).Build();

    /// <summary>Rehydrate <typeparamref name="TAggregate"/> from an event sequence.</summary>
    /// <typeparam name="TAggregate">The aggregate type — must have a public parameterless constructor.</typeparam>
    /// <param name="events">The events to apply, in order.</param>
    /// <returns>The rehydrated aggregate.</returns>
    public static TAggregate Rehydrate<TAggregate>(IEnumerable<object> events)
        where TAggregate : class, new()
    {
        ArgumentNullException.ThrowIfNull(events);
        return AggregateTestHarness<TAggregate>.Given(events as object[] ?? [.. events]).Build();
    }
}

internal static class AggregateEventApplier
{
    private static readonly ConcurrentDictionary<(Type Aggregate, Type Parameter), Action<object, object>?> DelegateCache = new();

    public static void Apply(object aggregate, object @event, long version, bool ignoreUnmapped)
    {
        var aggregateType = aggregate.GetType();
        var dataType = @event.GetType();

        var direct = GetApplyDelegate(aggregateType, dataType);
        if (direct is not null)
        {
            direct(aggregate, @event);
            return;
        }

        var wrappedType = typeof(IEvent<>).MakeGenericType(dataType);
        var wrapped = GetApplyDelegate(aggregateType, wrappedType);
        if (wrapped is not null)
        {
            wrapped(aggregate, WrapEvent(aggregate, @event, dataType, version));
            return;
        }

        if (!ignoreUnmapped)
        {
            throw new InvalidOperationException(
                $"Aggregate '{aggregateType.Name}' has no 'Apply({dataType.Name})' or 'Apply(IEvent<{dataType.Name}>)' method. " +
                $"Add the overload, or call IgnoringUnmappedEvents() to skip unmapped events as production does.");
        }
    }

    private static Action<object, object>? GetApplyDelegate(Type aggregateType, Type parameterType) =>
        DelegateCache.GetOrAdd((aggregateType, parameterType), static key =>
        {
            var method = key.Aggregate.GetMethod("Apply", [key.Parameter]);
            if (method is null)
            {
                return null;
            }

            var aggregateParam = Expression.Parameter(typeof(object), "aggregate");
            var eventParam = Expression.Parameter(typeof(object), "event");
            var call = Expression.Call(
                Expression.Convert(aggregateParam, key.Aggregate),
                method,
                Expression.Convert(eventParam, key.Parameter));
            return Expression.Lambda<Action<object, object>>(call, aggregateParam, eventParam).Compile();
        });

    private static object WrapEvent(object aggregate, object data, Type dataType, long version)
    {
        var streamId = aggregate is IAggregate a ? a.Id : Guid.Empty;
        var tenantId = aggregate is ITenantAggregate t ? t.TenantId : Guid.Empty;
        var eventType = typeof(Event<>).MakeGenericType(dataType);

        return Activator.CreateInstance(
            eventType,
            Guid.CreateVersion7(),
            version,
            data,
            streamId,
            tenantId,
            Guid.Empty,
            aggregate.GetType().Name)!;
    }
}
