using Stratara.Projections.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Reflections;

namespace Stratara.Projections.Services;

/// <summary>
/// Default <see cref="IProjectionHandler"/> that drives a single projection over a list of events,
/// using the cached delegates exposed by <see cref="IProjectionMethodInvoker"/>.
/// </summary>
/// <remarks>
/// Each event is first matched against a <c>HandleAsync(TEventPayload, CancellationToken)</c> overload;
/// if none exists, the handler falls back to a <c>HandleAsync(IEvent&lt;TEventPayload&gt;, CancellationToken)</c>
/// overload so handlers that need event metadata (stream id, version, …) can opt in.
/// </remarks>
internal sealed class ProjectionHandler(IProjectionMethodInvoker methodInvoker) : IProjectionHandler
{
    /// <inheritdoc/>
    public Type[] GetRelevantEventTypes(IProjection projection) => methodInvoker.GetOrCreateRelevantEventTypes(projection);

    /// <inheritdoc/>
    public string[] GetRelevantEventTypeNames(IProjection projection) =>
        GetRelevantEventTypes(projection).Select(t => t.GetQualifiedTypeName()).ToArray();

    /// <inheritdoc/>
    public string GetProjectionName(IProjection projection) => projection.GetType().Name;

    /// <inheritdoc/>
    public async Task ProjectAsync(IProjection projection, IReadOnlyList<IEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (var @event in events)
        {
            await HandleEventAsync(projection, @event, cancellationToken);
        }
    }

    private async Task HandleEventAsync(IProjection projection, IEvent @event, CancellationToken cancellationToken)
    {
        if (await TryHandleEventAsync(projection, @event, cancellationToken))
        {
            return;
        }

        await TryHandleWrappedEventAsync(projection, @event, cancellationToken);
    }

    private async Task<bool> TryHandleEventAsync(IProjection projection, IEvent @event, CancellationToken cancellationToken)
    {
        var eventDataType = @event.Data.GetType();
        var handleDelegate = methodInvoker.GetOrCreateDelegate(projection, eventDataType);
        if (methodInvoker.IsNoOp(handleDelegate))
        {
            return false;
        }

        await handleDelegate(projection, @event.Data, cancellationToken);
        return true;
    }

    private Task TryHandleWrappedEventAsync(IProjection projection, IEvent @event, CancellationToken cancellationToken)
    {
        var eventDataType = @event.Data.GetType();
        var eventInterfaceType = typeof(IEvent<>).MakeGenericType(eventDataType);
        var handleDelegate = methodInvoker.GetOrCreateDelegate(projection, eventInterfaceType);
        return handleDelegate(projection, @event, cancellationToken);
    }
}
