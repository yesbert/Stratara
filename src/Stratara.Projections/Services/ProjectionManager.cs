using Microsoft.Extensions.Logging;
using Stratara.Projections.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Projections.Services;

/// <summary>
/// Default <see cref="IProjectionManager"/>. Fans the incoming event bundle out across every registered
/// projection in parallel, filtering each projection's relevant events by their declared <c>HandleAsync</c>
/// overload signatures and delegating to <see cref="IProjectionHandler"/>.
/// </summary>
internal sealed class ProjectionManager(
    ILogger<ProjectionManager> logger,
    IProjectionHandler projectionHandler,
    IEnumerable<IProjection> projections) : IProjectionManager
{
    /// <inheritdoc/>
    public async Task HandleAsync(IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(projections, cancellationToken,
            async (projection, token) => { await RunProjectionIfRelevant(projection, events, token); });
    }

    private async Task RunProjectionIfRelevant(IProjection projection, IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
    {
        var relevantTypeNames = projectionHandler.GetRelevantEventTypeNames(projection);
        var projectionName = projectionHandler.GetProjectionName(projection);
        var relevantEvents = events.Where(e => relevantTypeNames.Contains(e.EventTypeName)).ToList();

        if (relevantEvents.Count == 0)
        {
            logger.LogEventsNotRelevantForProjection(events.Count, new DistinctEventTypeNames(events), projectionName);
            return;
        }

        await RunProjection(projection, relevantEvents, cancellationToken);
    }

    private async Task RunProjection(IProjection projection, IReadOnlyList<IEvent> relevantEvents, CancellationToken cancellationToken)
    {
        await projectionHandler.ProjectAsync(projection, relevantEvents, cancellationToken);
    }
}
