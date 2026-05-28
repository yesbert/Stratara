using System.Collections.Concurrent;
using System.Linq.Expressions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Default <see cref="IEventTypeResolver"/> that lifts an <see cref="IEvent"/> with runtime payload
/// type <c>T</c> into the strongly-typed <see cref="IEvent{T}"/> shape, caching the conversion
/// delegate per payload type.
/// </summary>
internal sealed class EventTypeResolver : IEventTypeResolver
{
    private static readonly ConcurrentDictionary<Type, Func<IEvent, object>> ResolverCache = new();

    /// <inheritdoc/>
    public IEvent<T> Resolve<T>(IEvent @event) where T : notnull
    {
        if (@event is IEvent<T> typed)
        {
            return typed;
        }

        var resolved = ResolveDynamic(@event);
        return (IEvent<T>)resolved;
    }

    /// <inheritdoc/>
    public object ResolveDynamic(IEvent @event)
    {
        var dataType = @event.Data.GetType();
        var resolver = ResolverCache.GetOrAdd(dataType, BuildResolver);
        return resolver(@event);
    }

    /// <inheritdoc/>
    public Type GetEventDataType(IEvent @event) => @event.Data.GetType();

    private static Func<IEvent, object> BuildResolver(Type eventType)
    {
        var wrapperType = typeof(IEvent<>).MakeGenericType(eventType);

        var param = Expression.Parameter(typeof(IEvent), "evt");
        var convert = Expression.Convert(param, wrapperType);

        return Expression.Lambda<Func<IEvent, object>>(convert, param).Compile();
    }
}
