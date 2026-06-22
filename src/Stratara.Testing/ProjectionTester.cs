using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Testing;

/// <summary>
/// Invokes a projection's event handler directly in a test. Stratara projections declare their
/// <c>HandleAsync</c> methods as private (the runtime discovers them by reflection), so a unit test
/// cannot call them without this helper. Pair it with mocked repositories / unit-of-work to assert
/// what the projection wrote.
/// </summary>
public static class ProjectionTester
{
    private const string HandlerMethodName = "HandleAsync";

    /// <summary>
    /// Dispatch <paramref name="event"/> to <paramref name="projection"/>'s handler — trying
    /// <c>HandleAsync(IEvent&lt;TData&gt;, CancellationToken)</c> first, then
    /// <c>HandleAsync(TData, CancellationToken)</c>.
    /// </summary>
    /// <typeparam name="TData">The event payload type.</typeparam>
    /// <param name="projection">The projection instance (its handler may be public or private).</param>
    /// <param name="event">The event to dispatch — build one with <see cref="TestEvent.Create{TData}"/>.</param>
    /// <param name="cancellationToken">Propagated to the handler.</param>
    /// <returns>The task returned by the projection handler.</returns>
    /// <exception cref="InvalidOperationException">The projection has no matching <c>HandleAsync</c> overload.</exception>
    public static Task HandleAsync<TData>(object projection, IEvent<TData> @event, CancellationToken cancellationToken = default)
        where TData : notnull
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(@event);

        var projectionType = projection.GetType();

        var wrapped = FindHandler(projectionType, typeof(IEvent<TData>));
        if (wrapped is not null)
        {
            return (Task)wrapped.Invoke(projection, [@event, cancellationToken])!;
        }

        var bare = FindHandler(projectionType, typeof(TData));
        if (bare is not null)
        {
            return (Task)bare.Invoke(projection, [@event.Data, cancellationToken])!;
        }

        throw new InvalidOperationException(
            $"'{projectionType.Name}' has no '{HandlerMethodName}(IEvent<{typeof(TData).Name}>, CancellationToken)' " +
            $"or '{HandlerMethodName}({typeof(TData).Name}, CancellationToken)' method.");
    }

    [SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields",
        Justification = "Stratara projections declare HandleAsync as a non-public member (the runtime discovers it by reflection); " +
                        "this test helper must reflect over non-public members to invoke the handler under test.")]
    private static MethodInfo? FindHandler(Type projectionType, Type firstParameterType) =>
        projectionType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == HandlerMethodName
                && m.GetParameters() is [var first, var second]
                && first.ParameterType == firstParameterType
                && second.ParameterType == typeof(CancellationToken));
}
