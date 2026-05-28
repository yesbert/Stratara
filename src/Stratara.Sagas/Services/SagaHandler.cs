using Stratara.Sagas.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Reflections;

namespace Stratara.Sagas.Services;

/// <summary>
/// Default <see cref="ISagaHandler"/>. For each incoming event it first tries the saga's plain
/// <c>HandleAsync(TEvent)</c> overload, then the wrapped <c>HandleAsync(IEvent&lt;TEvent&gt;)</c> overload.
/// </summary>
internal sealed class SagaHandler(ISagaMethodInvoker methodInvoker) : ISagaHandler
{
    /// <inheritdoc/>
    public Type[] GetRelevantEventTypes(ISaga saga) => methodInvoker.GetOrCreateRelevantEventTypes(saga);

    /// <inheritdoc/>
    public string[] GetRelevantEventTypeNames(ISaga saga) =>
        GetRelevantEventTypes(saga).Select(t => t.GetQualifiedTypeName()).ToArray();

    /// <inheritdoc/>
    public string GetSagaName(ISaga saga) => saga.GetType().Name;

    /// <inheritdoc/>
    public async Task HandleAsync(ISaga saga, IReadOnlyList<IEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (var @event in events)
        {
            await HandleEventAsync(saga, @event, cancellationToken);
        }
    }

    private async Task HandleEventAsync(ISaga saga, IEvent @event, CancellationToken cancellationToken)
    {
        if (await TryHandleEventAsync(saga, @event, cancellationToken))
        {
            return;
        }

        await TryHandleWrappedEventAsync(saga, @event, cancellationToken);
    }

    private async Task<bool> TryHandleEventAsync(ISaga saga, IEvent @event, CancellationToken cancellationToken)
    {
        var eventDataType = @event.Data.GetType();
        var handleDelegate = methodInvoker.GetOrCreateDelegate(saga, eventDataType);
        if (methodInvoker.IsNoOp(handleDelegate))
        {
            return false;
        }

        await handleDelegate(saga, @event.Data, cancellationToken);
        return true;
    }

    private Task TryHandleWrappedEventAsync(ISaga saga, IEvent @event, CancellationToken cancellationToken)
    {
        var eventDataType = @event.Data.GetType();
        var eventInterfaceType = typeof(IEvent<>).MakeGenericType(eventDataType);
        var handleDelegate = methodInvoker.GetOrCreateDelegate(saga, eventInterfaceType);
        return handleDelegate(saga, @event, cancellationToken);
    }
}
