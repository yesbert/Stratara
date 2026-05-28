using Microsoft.Extensions.Logging;
using Stratara.Sagas.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Sagas.Services;

/// <summary>
/// Default <see cref="ISagaManager"/>. Fans the incoming event bundle out across every registered saga
/// in parallel, filtering each saga's relevant events by their declared <c>HandleAsync</c> overload
/// signatures and delegating to <see cref="ISagaHandler"/>.
/// </summary>
internal sealed class SagaManager(
    ILogger<SagaManager> logger,
    ISagaHandler sagaHandler,
    IEnumerable<ISaga> sagas) : ISagaManager
{
    /// <inheritdoc/>
    public async Task HandleAsync(IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(sagas, cancellationToken,
            async (saga, token) => { await RunSagaIfRelevant(saga, events, token); });
    }

    private async Task RunSagaIfRelevant(ISaga saga, IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
    {
        var relevantTypeNames = sagaHandler.GetRelevantEventTypeNames(saga);
        var sagaName = sagaHandler.GetSagaName(saga);
        var relevantEvents = events.Where(e => relevantTypeNames.Contains(e.EventTypeName)).ToList();

        if (relevantEvents.Count == 0)
        {
            logger.LogEventsNotRelevantForSaga(events.Count, new DistinctEventTypeNames(events), sagaName);
            return;
        }

        await RunSaga(saga, relevantEvents, cancellationToken);
    }

    private async Task RunSaga(ISaga saga, IReadOnlyList<IEvent> relevantEvents, CancellationToken cancellationToken)
    {
        await sagaHandler.HandleAsync(saga, relevantEvents, cancellationToken);
    }
}
