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
/// running cluster — empties the projection, returns the checkpoints to the beginning once more, and resumes the
/// readers however the truncation ended. A pause that fails leaves none of them paused: what had paused is resumed and
/// the rebuild fails. The first reset comes before the truncation, so no checkpoint is ever left past an effect the
/// truncation removed; a truncation that fails part-way leaves readers that re-read from the beginning over a partly
/// emptied model, which re-applying repairs. The second reset undoes whatever a reader applied before the truncation
/// although it should have been paused — its pause lapsed, or its activation moved and forgot the pause — so those
/// facts are applied again after it. The readers hold the rebuild's pause apart from any other, so two rebuilds of one
/// projection may overlap and the readers resume only when the last has finished. A rebuild while a full replay is
/// active is refused: the replay empties and refills every projection itself.
/// </summary>
internal sealed class ProjectionRebuilder(
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CommitOrderOptions> commitOrder,
    IProjectionReplayState replayState,
    StoreReaderLease lease) : IProjectionRebuilder
{
    /// <exception cref="InvalidOperationException">A full replay is active, or the projection is unknown or not rebuildable.</exception>
    public async Task RebuildAsync(string projectionName, CancellationToken cancellationToken = default)
    {
        if (replayState.IsReplayActive)
        {
            throw new InvalidOperationException($"Projection '{projectionName}' cannot be rebuilt while a full replay is active: the replay empties and refills every projection, this one included. Rebuild it once the replay has ended.");
        }

        var partitions = Enumerable.Range(0, commitOrder.Value.PartitionCount)
            .Select(partition => Reader(StoreReaderGrainKey.Of(projectionName, partition)))
            .ToList();

        await using var hold = await StoreReaderPause.PauseAllAsync(partitions, lease);

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
        await TruncationBetweenResets.RunAsync(
            token => Task.WhenAll(Enumerable.Range(0, commitOrder.Value.PartitionCount)
                .Select(partition => checkpoints.ResetAsync(projectionName, partition, reader, token))),
            rebuildable.TruncateAsync,
            cancellationToken);

        await hold.ResumeAsync();
    }

    private PausedReader Reader(string key)
    {
        var grain = grainFactory.GetGrain<IProjectionGrain>(key);
        return new PausedReader(key, grain.PauseAsync, grain.RenewPauseAsync, grain.ResumeAsync);
    }
}

/// <summary>
/// The order a rebuild and a replay change a read model in: checkpoints to the beginning, the model emptied,
/// checkpoints to the beginning again. The second reset runs however the truncation ended — without the caller's
/// token where the truncation failed, so a cancelled truncation still leaves no checkpoint past an effect it removed.
/// </summary>
internal static class TruncationBetweenResets
{
    /// <exception cref="AggregateException">The truncation failed, and so did the reset after it.</exception>
    public static async Task RunAsync(Func<CancellationToken, Task> reset, Func<CancellationToken, Task> truncate, CancellationToken cancellationToken)
    {
        await reset(cancellationToken);
        try
        {
            await truncate(cancellationToken);
        }
        catch (Exception truncation)
        {
            if (await ResetQuietlyAsync(reset) is { } failure)
            {
                throw new AggregateException("Emptying the read model failed, and so did returning the checkpoints to the beginning after it.", truncation, failure);
            }

            throw;
        }

        await reset(cancellationToken);
    }

    private static async Task<Exception?> ResetQuietlyAsync(Func<CancellationToken, Task> reset)
    {
        try
        {
            await reset(CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
