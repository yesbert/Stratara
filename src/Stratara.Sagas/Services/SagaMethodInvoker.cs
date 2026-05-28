using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Stratara.Sagas.Abstractions;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Sagas.Services;

/// <summary>
/// Reflection-based <see cref="ISagaMethodInvoker"/> with process-lifetime caches. Discovers any
/// <c>HandleAsync(TEvent, CancellationToken)</c> method on a saga (public or private), compiles a delegate
/// that calls it, and memoizes both the delegate and the set of relevant event types.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache lifecycle.</b> Two static <see cref="ConcurrentDictionary{TKey,TValue}"/> caches grow
/// for the life of the process — one entry per saga-type / event-type combination, plus one entry
/// per saga-type for the relevant-event-types lookup. Entries are added on first dispatch and never
/// evicted. The implementation assumes the host registers a finite set of sagas and event types
/// at startup, so the cache plateaus quickly.
/// </para>
/// <para>
/// <b>Unbounded growth risk.</b> Hosts that dynamically load sagas or define new event types at
/// runtime (e.g. plugin systems, hot-reload setups, or test-suites that compile fresh saga
/// assemblies per test) will see the caches grow without bound. In that scenario either monitor the
/// process memory footprint and accept it, or register a custom <see cref="ISagaMethodInvoker"/>
/// implementation that bounds the caches (LRU or size-limited).
/// </para>
/// </remarks>
internal sealed class SagaMethodInvoker : ISagaMethodInvoker
{
    private const string HandleAsyncMethodName = "HandleAsync";

    [SuppressMessage(
        "Major Code Smell",
        "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields",
        Justification = "Sagas may declare HandleAsync methods with private visibility; the dispatcher must invoke them regardless of access modifier.")]
    private const BindingFlags SagaHandlerBindingFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Func<ISaga, object, CancellationToken, Task> NoOp = (_, _, _) => Task.CompletedTask;

    private static readonly ConcurrentDictionary<(Type sagaType, Type eventType), Func<ISaga, object, CancellationToken, Task>>
        HandleAsyncDelegatesCache = new();

    private static readonly ConcurrentDictionary<Type, Type[]> RelevantEventTypesCache = new();

    /// <inheritdoc/>
    public Type[] GetOrCreateRelevantEventTypes(ISaga saga)
    {
        var key = saga.GetType();
        if (RelevantEventTypesCache.TryGetValue(key, out var types))
        {
            return types;
        }

        var relevantTypes = saga.GetType()
            .GetMethods(SagaHandlerBindingFlags) // NOSONAR
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
    public Func<ISaga, object, CancellationToken, Task> GetOrCreateDelegate(ISaga saga, Type eventType)
    {
        var key = (saga.GetType(), eventType);
        return HandleAsyncDelegatesCache.GetOrAdd(key, static key => CreateHandleAsyncDelegate(key.sagaType, key.eventType));
    }

    /// <inheritdoc/>
    public bool IsNoOp(Func<ISaga, object, CancellationToken, Task> delegateOperation) =>
        ReferenceEquals(delegateOperation, NoOp);

    private static Func<ISaga, object, CancellationToken, Task> CreateHandleAsyncDelegate(Type sagaType, Type eventType)
    {
        var method = sagaType.GetMethods(SagaHandlerBindingFlags) // NOSONAR
            .FirstOrDefault(m =>
                m.Name == HandleAsyncMethodName &&
                m.ReturnType == typeof(Task) &&
                m.GetParameters().Length == 2 &&
                m.GetParameters().FirstOrDefault()?.ParameterType == eventType);

        if (method is null)
        {
            return NoOp;
        }

        var instanceParam = Expression.Parameter(typeof(ISaga), "instance");
        var eventParam = Expression.Parameter(typeof(object), "event");
        var cancellationParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var castInstance = Expression.Convert(instanceParam, sagaType);
        var castEvent = Expression.Convert(eventParam, eventType);
        var call = Expression.Call(castInstance, method, castEvent, cancellationParam);
        return Expression.Lambda<Func<ISaga, object, CancellationToken, Task>>(call, instanceParam, eventParam, cancellationParam).Compile();
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
