using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Orleans.CommitOrder;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// A projection that can be rebuilt on its own: it knows how to empty what it wrote. The
/// framework's replay truncates every read model at once; a projection that opts in here is rebuilt
/// alone while every other projection keeps applying.
/// </summary>
public interface IRebuildableProjection : IProjection
{
    /// <summary>Empties this projection's read model, and nothing else.</summary>
    /// <param name="cancellationToken">Propagated to the store.</param>
    Task TruncateAsync(CancellationToken cancellationToken);
}

/// <summary>Rebuilds one projection from the beginning of the store.</summary>
public interface IProjectionRebuilder
{
    /// <summary>
    /// Pauses the projection's grains, empties its read model, resets its checkpoints and resumes the
    /// grains at once, which read the store from the start in parallel, one per partition. Returns
    /// when the grains are resumed, not when they have caught up.
    /// </summary>
    /// <param name="projectionName">The projection, by the name the framework gives it.</param>
    /// <param name="cancellationToken">Propagated to the truncation and the checkpoint store.</param>
    /// <exception cref="InvalidOperationException">No projection of that name is registered, or it is not rebuildable.</exception>
    Task RebuildAsync(string projectionName, CancellationToken cancellationToken = default);
}

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
            var reader = services.GetRequiredService<ICommittedPositionReader>().GetType().Name;
            await Task.WhenAll(Enumerable.Range(0, commitOrder.Value.PartitionCount)
                .Select(partition => checkpoints.SetAsync(projectionName, partition, reader, 0, cancellationToken)));
        }
        finally
        {
            await Task.WhenAll(partitions.Select(grain => grain.ResumeAsync()));
        }
    }
}
