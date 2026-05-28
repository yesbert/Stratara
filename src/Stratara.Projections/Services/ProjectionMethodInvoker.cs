using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Stratara.Projections.Abstractions;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Projections.Services;

/// <summary>
/// Default <see cref="IProjectionMethodInvoker"/>. Reflects over a projection's <c>HandleAsync</c> overloads,
/// compiles a <see cref="Expression"/>-tree delegate per <c>(projectionType, eventType)</c> pair, and caches
/// both the delegate and the set of relevant event types for the lifetime of the process.
/// </summary>
/// <remarks>
/// Both caches grow only on first dispatch of a previously unseen combination and never shrink. The
/// assumption is that a host registers a finite set of projections and a finite event-type taxonomy at
/// startup — the cache plateaus quickly. If a host loads projections or event types dynamically at runtime,
/// monitor the cache size and consider swapping in a custom <see cref="IProjectionMethodInvoker"/>
/// implementation.
/// </remarks>
internal sealed class ProjectionMethodInvoker : IProjectionMethodInvoker
{
    private const string HandleAsyncMethodName = "HandleAsync";
    private static readonly Func<IProjection, object, CancellationToken, Task> NoOp = (_, _, _) => Task.CompletedTask;

    private static readonly ConcurrentDictionary<(Type projectionType, Type eventType), Func<IProjection, object, CancellationToken, Task>>
        HandleAsyncDelegatesCache = new();

    private static readonly ConcurrentDictionary<Type, Type[]> RelevantEventTypesCache = new();

    /// <inheritdoc/>
    public Type[] GetOrCreateRelevantEventTypes(IProjection projection)
    {
        var key = projection.GetType();
        if (RelevantEventTypesCache.TryGetValue(key, out var types))
        {
            return types;
        }

        var relevantTypes = projection.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) // NOSONAR — intentional: discovers private HandleAsync methods in projections
            .Where(m =>
                m.Name == HandleAsyncMethodName &&
                m.ReturnType == typeof(Task) &&
                m.GetParameters().Length == 2)
            .Select(m => m.GetParameters())
            .Select(GetFirstParameterType)
            .Distinct()
            .ToArray();
        RelevantEventTypesCache.TryAdd(key, relevantTypes);
        return relevantTypes;
    }

    /// <inheritdoc/>
    public Func<IProjection, object, CancellationToken, Task> GetOrCreateDelegate(IProjection projection, Type eventType)
    {
        var key = (projection.GetType(), eventType);
        return HandleAsyncDelegatesCache.GetOrAdd(key, static key => CreateHandleAsyncDelegate(key.projectionType, key.eventType));
    }

    /// <inheritdoc/>
    public bool IsNoOp(Func<IProjection, object, CancellationToken, Task> delegateOperation) =>
        ReferenceEquals(delegateOperation, NoOp);

    private static Func<IProjection, object, CancellationToken, Task> CreateHandleAsyncDelegate(Type projectionType, Type eventType)
    {
        var method = projectionType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) // NOSONAR — intentional: discovers private HandleAsync methods in projections
            .FirstOrDefault(m =>
                m.Name == HandleAsyncMethodName &&
                m.ReturnType == typeof(Task) &&
                m.GetParameters().Length == 2 &&
                m.GetParameters().FirstOrDefault()?.ParameterType == eventType);

        if (method is null)
        {
            return NoOp;
        }

        var instanceParam = Expression.Parameter(typeof(IProjection), "instance");
        var eventParam = Expression.Parameter(typeof(object), "event");
        var cancellationParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var castInstance = Expression.Convert(instanceParam, projectionType);
        var castEvent = Expression.Convert(eventParam, eventType);
        var call = Expression.Call(castInstance, method, castEvent, cancellationParam);
        return Expression.Lambda<Func<IProjection, object, CancellationToken, Task>>(call, instanceParam, eventParam, cancellationParam).Compile();
    }

    private static Type GetFirstParameterType(ParameterInfo[] parameters)
    {
        var paramType = parameters[0].ParameterType;
        if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(IEvent<>))
        {
            return paramType.GetGenericArguments()[0];
        }

        return paramType;
    }
}