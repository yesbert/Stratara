using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Rebuilds one projection: pauses its readers, returns their checkpoints to the beginning, empties the
/// projection, and resumes the readers however the truncation ended. The reset comes first, so no checkpoint
/// is ever left past an effect the truncation removed; a truncation that fails part-way leaves readers that
/// re-read from the beginning over a partly emptied model, which re-applying repairs.
/// </summary>
internal sealed class ProjectionRebuilder(
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CommitOrderOptions> commitOrder) : IProjectionRebuilder
{
    public async Task RebuildAsync(string projectionName, CancellationToken cancellationToken = default)
    {
        var partitions = Enumerable.Range(0, commitOrder.Value.PartitionCount)
            .Select(partition => grainFactory.GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(projectionName, partition)))
            .ToList();

        await Task.WhenAll(partitions.Select(grain => grain.PauseAsync()));

        try
        {
            using var scope = scopeFactory.CreateScope();
            var services = scope.ServiceProvider;
            var handler = services.GetRequiredService<IProjectionHandler>();
            var projection = services.GetServices<IProjection>().FirstOrDefault(p => handler.GetProjectionName(p) == projectionName)
                             ?? throw new InvalidOperationException($"No projection named '{projectionName}' is registered.");
            if (projection is not IRebuildableProjection rebuildable)
            {
                throw new InvalidOperationException($"Projection '{projectionName}' does not implement {nameof(IRebuildableProjection)} and cannot be rebuilt on its own.");
            }

            var checkpoints = services.GetRequiredService<IProjectionCheckpointStore>();
            var reader = services.GetRequiredService<ICommittedPositionReader>().Name;
            await Task.WhenAll(Enumerable.Range(0, commitOrder.Value.PartitionCount)
                .Select(partition => checkpoints.SetAsync(projectionName, partition, reader, 0, cancellationToken)));

            await rebuildable.TruncateAsync(cancellationToken);
        }
        finally
        {
            await Task.WhenAll(partitions.Select(grain => grain.ResumeAsync()));
        }
    }
}
