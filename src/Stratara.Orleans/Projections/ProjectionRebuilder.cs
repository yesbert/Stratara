using System.Runtime.ExceptionServices;
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
/// running cluster — empties the projection, waits for any batch a reader had in flight, returns the checkpoints to the
/// beginning once more, and resumes the readers however the truncation ended. A pause that fails leaves none of them paused: what had paused is resumed and
/// the rebuild fails. The first reset comes before the truncation, so no checkpoint is ever left past an effect the
/// truncation removed; a truncation that fails part-way leaves readers that re-read from the beginning over a partly
/// emptied model, which re-applying repairs. The second reset undoes whatever a reader applied before the truncation
/// although it should have been paused — its pause lapsed, or its activation moved and forgot the pause — so those
/// facts are applied again after it. The readers hold the rebuild's pause apart from any other, so two rebuilds of one
/// projection may overlap and the readers resume only when the last has finished. A rebuild while a full replay is
/// active is refused: the replay empties and refills every projection itself. Where an <see cref="IForgottenTenantStore"/>
/// is registered, the projection's record of deleted tenants is emptied together with its read model.
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
        var forgottenTenants = services.GetService<IForgottenTenantStore>();
        await TruncationBetweenResets.RunAsync(
            token => Task.WhenAll(Enumerable.Range(0, commitOrder.Value.PartitionCount)
                .Select(partition => checkpoints.ResetAsync(projectionName, partition, reader, token))),
            async token =>
            {
                await rebuildable.TruncateAsync(token);
                if (forgottenTenants is not null)
                {
                    await forgottenTenants.ClearAsync(projectionName, token);
                }
            },
            hold.QuiesceAsync,
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
/// The order a rebuild and a replay change a read model in: checkpoints to the beginning, the model emptied, the
/// readers quiesced, checkpoints to the beginning again. The quiescing waits for any batch a reader had in flight — one
/// that read while it should have been paused — so its checkpoint is written before the second reset rather than over
/// it. The quiescing and the second reset run however the truncation ended, and the reset without the caller's token:
/// once the model has been emptied, no checkpoint may be left past an effect the truncation removed.
/// </summary>
internal static class TruncationBetweenResets
{
    /// <exception cref="AggregateException">More than one of the truncation, the quiescing and the second reset failed.</exception>
    public static async Task RunAsync(Func<CancellationToken, Task> reset, Func<CancellationToken, Task> truncate, Func<Task> quiesce, CancellationToken cancellationToken)
    {
        await reset(cancellationToken);
        var truncation = await CaptureAsync(() => truncate(cancellationToken));
        var quiescing = await CaptureAsync(quiesce);
        var repair = await CaptureAsync(() => reset(CancellationToken.None));
        List<Exception> failures = [.. new[] { truncation, quiescing, repair }.OfType<Exception>()];
        switch (failures.Count)
        {
            case 0:
                return;
            case 1:
                ExceptionDispatchInfo.Throw(failures[0]);
                return;
            default:
                throw new AggregateException("Emptying the read model and returning the checkpoints to the beginning after it did not both succeed.", failures);
        }
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> step)
    {
        try
        {
            await step();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
