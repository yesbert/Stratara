using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

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

            await rebuildable.TruncateAsync(cancellationToken);

            var checkpoints = services.GetRequiredService<IProjectionCheckpointStore>();
            var reader = services.GetRequiredService<ICommittedPositionReader>().Name;
            await Task.WhenAll(Enumerable.Range(0, commitOrder.Value.PartitionCount)
                .Select(partition => checkpoints.SetAsync(projectionName, partition, reader, 0, cancellationToken)));
        }
        finally
        {
            await Task.WhenAll(partitions.Select(grain => grain.ResumeAsync()));
        }
    }
}
