using System.Reflection;
using Stratara.Projections.Abstractions;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Projections.Tests.Helpers;

public static class ProjectionTestHelper
{
    public static Task HandleAsync<TEvent>(IProjection projection, IEvent<TEvent> @event,
        CancellationToken ct = default) where TEvent : notnull
    {
        var eventType = typeof(IEvent<TEvent>);
        var method = projection.GetType()
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == "HandleAsync"
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == eventType
                && m.GetParameters()[1].ParameterType == typeof(CancellationToken));

        if (method is null)
        {
            throw new InvalidOperationException(
                $"No HandleAsync(IEvent<{typeof(TEvent).Name}>, CancellationToken) method found on {projection.GetType().Name}");
        }

        return (Task)method.Invoke(projection, [@event, ct])!;
    }
}
