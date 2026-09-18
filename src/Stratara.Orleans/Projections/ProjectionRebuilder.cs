using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Rebuilds one projection: pauses its readers, returns their checkpoints to the beginning — whatever reader
/// name they were written under, so a host whose reader or partition count changed rebuilds from inside the
/// running cluster — empties the projection, and resumes the readers however the truncation ended. A pause that
/// fails leaves none of them paused: what had paused is resumed and the rebuild fails. The reset comes first, so no checkpoint
/// is ever left past an effect the truncation removed; a truncation that fails part-way leaves readers that
/// re-read from the beginning over a partly emptied model, which re-applying repairs. Two rebuilds of one
/// projection may overlap: the readers count their pausers and resume only when the last has finished. A rebuild
/// while a full replay is active is refused: the replay empties and refills every projection itself.
/// </summary>
internal sealed class ProjectionRebuilder(
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CommitOrderOptions> commitOrder,
    IProjectionReplayState replayState) : IProjectionRebuilder
{
    /// <exception cref="InvalidOperationException">A full replay is active, or the projection is unknown or not rebuildable.</exception>
    public async Task RebuildAsync(string projectionName, CancellationToken cancellationToken = default)
    {
        if (replayState.IsReplayActive)
        {
            throw new InvalidOperationException($"Projection '{projectionName}' cannot be rebuilt while a full replay is active: the replay empties and refills every projection, this one included. Rebuild it once the replay has ended.");
        }

        var partitions = Enumerable.Range(0, commitOrder.Value.PartitionCount)
            .Select(partition => new PausedReader(
                StoreReaderGrainKey.Of(projectionName, partition),
                grainFactory.GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(projectionName, partition))))
            .ToList();

        var paused = await StoreReaderPause.PauseAllAsync(partitions);

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
                .Select(partition => checkpoints.ResetAsync(projectionName, partition, reader, cancellationToken)));

            await rebuildable.TruncateAsync(cancellationToken);
        }
        catch
        {
            await StoreReaderPause.ResumeQuietlyAsync(paused);
            throw;
        }

        await StoreReaderPause.ResumeAllAsync(paused);
    }
}
