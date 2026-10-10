using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Session;
using Stratara.Projections.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Resilience;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Projections.Services;

/// <summary>
/// Background service that replays the full event stream against all projections on demand. Triggered via
/// <see cref="IProjectionReplayState"/>; truncates all projection views — having first emptied the record of deleted
/// tenants of each registered projection that declares <see cref="IForgetsDeletedTenants"/> — and re-applies every
/// event, batched by <see cref="ProjectionOptions.BatchSize"/>, each stream in version order and the streams
/// interleaved as their sequence numbers interleave them.
/// </summary>
/// <remarks>
/// <para>
/// A save does not number its entries in version order, so a batch is read through
/// <see cref="IEventStreamRepository.GetManyAfterSequenceInStreamOrderAsync"/>, which may return more entries than
/// the batch size and whose last entry need not carry its highest sequence number; the next batch starts after
/// the highest. Each batch is processed in a fresh DI scope so the unit-of-work and session context lifecycle matches
/// what real-time projection dispatch sees. Each batch — reading it and applying it — runs under the
/// <see cref="ResilienceNames.ProjectionReplayBatch"/> policy: a failed attempt disposes its scope and the
/// batch is applied again from its first entry in a new one, so a passing failure such as a read-store
/// timeout does not end the replay. Once the attempts are exhausted the failure ends the replay as an
/// unretried one would.
/// </para>
/// <para>
/// A request runs only if this host claims it through <see cref="IProjectionReplayState.TryActivate"/>, and the replay
/// ends in one <see cref="IProjectionReplayState.Complete"/> — succeeded, failed with its message truncated to 500
/// characters, or interrupted by the host stopping — so consumer-side dashboards can read its outcome after it ended.
/// While it preserves, empties and counts, none of which reports progress, it renews its marking every
/// <see cref="PreparationRenewal"/>.
/// </para>
/// <para>
/// Where an <see cref="IReadModelPreservation"/> is registered, the replay preserves the read models before it empties
/// anything, restores them when it fails and drops the preserved state when it succeeds; a replay its host stops leaves
/// it. Before the host takes requests it restores a state such a replay left, and while a replay that is still marked
/// active owns that state, it checks again every <see cref="AbandonedCheckInterval"/>. No replay starts on its own.
/// </para>
/// </remarks>
internal sealed class ProjectionReplayWorker(
    ILogger<ProjectionReplayWorker> logger,
    IServiceScopeFactory scopeFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<ProjectionOptions> options,
    TimeProvider? timeProvider = null) : BackgroundService
{
    /// <summary>
    /// How often the replay renews its marking while it prepares — preserves the read models, empties them, counts the
    /// events — since none of those steps reports progress. Shorter than the shortest lease the options accept.
    /// </summary>
    internal static readonly TimeSpan PreparationRenewal = TimeSpan.FromSeconds(1);

    /// <summary>How often a host checks again for a preserved state that a replay still marked active owned.</summary>
    internal static readonly TimeSpan AbandonedCheckInterval = TimeSpan.FromSeconds(30);

    private const int MaxFailureMessageLength = 500;

    private readonly ProjectionOptions _options = options.Value;
    private readonly ResiliencePipeline _batchPipeline = pipelineProvider.GetPipeline(ResilienceNames.ProjectionReplayBatch);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var abandoned = await RestoreAbandonedReadModelsAsync(stoppingToken);
        await replayState.SubscribeToReplayRequestAsync(
            requestId => TrackAsync(requestId, stoppingToken), stoppingToken);
        if (abandoned is null or AbandonedPreservation.StillOwned)
        {
            await CheckAbandonedReadModelsAgainAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Where the host keeps the read models a replay empties, restores a state left by a replay whose host stopped —
    /// before the host takes requests, so a request cannot preserve the partial read models in its place. A failure here,
    /// a misregistration included, is logged and does not stop the host: it answers <see langword="null"/>, the preserved
    /// state is kept, and the host checks again.
    /// </summary>
    private async Task<AbandonedPreservation?> RestoreAbandonedReadModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IReadModelPreservation>() is not { } preservation)
            {
                return AbandonedPreservation.NoneKept;
            }

            var found = await preservation.RestoreAbandonedAsync(cancellationToken);
            if (found == AbandonedPreservation.Restored)
            {
                logger.LogAbandonedReadModelsRestored();
            }

            return found;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AbandonedPreservation.NoneKept;
        }
        catch (Exception ex)
        {
            logger.LogReadModelRestoreFailed(ex);
            return null;
        }
    }

    /// <summary>
    /// A preserved state was still owned by a replay marked active — a host that restarted within its own dead replay's
    /// lease finds it so — or the check failed. Once the marking lapses the state is abandoned, so the host checks again
    /// until it is restored, dropped, or taken over by a replay that ends it itself.
    /// </summary>
    private async Task CheckAbandonedReadModelsAgainAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(AbandonedCheckInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (await RestoreAbandonedReadModelsAsync(cancellationToken) is not (null or AbandonedPreservation.StillOwned))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: the preserved state is kept for the next start.
        }
    }

    /// <summary>
    /// Waits, within the host's shutdown timeout, for a replay that is running to record that it was interrupted:
    /// the subscription returned long ago, so without this the host would dispose the coordination store while the
    /// replay still writes its outcome to it.
    /// </summary>
    /// <param name="cancellationToken">Signals that the host's shutdown timeout has passed.</param>
    /// <returns>A task that completes once every running replay has ended, or the timeout has passed.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await Task.WhenAll(_running.Values).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The shutdown timeout passed; the replay's marking lapses with its lease.
        }
    }

    private async Task TrackAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var key = Guid.NewGuid();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running[key] = ended.Task;
        try
        {
            await RunRequestedReplayAsync(requestId, cancellationToken);
        }
        finally
        {
            _running.TryRemove(key, out _);
            ended.TrySetResult();
        }
    }

    /// <summary>
    /// Runs the replay a request asks for, if this host claims the request: a request every host receives, one that
    /// arrives while another replay is active, or one that arrives while the host stops, starts nothing here. Where the
    /// host keeps the read models, they are preserved before anything is emptied, restored after a failure and dropped
    /// after a success; a replay its host stops leaves them for the next start. The replay ends in exactly one
    /// completion — succeeded, failed with its message, or interrupted because the host stops — which keeps its
    /// outcome. Nothing here may escape: the coordination store invokes the callback where an exception would end the
    /// process.
    /// </summary>
    private async Task RunRequestedReplayAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        bool claimed;
        try
        {
            claimed = replayState.TryActivate(requestId);
        }
        catch (Exception ex)
        {
            logger.LogProjectionReplayRequestNotClaimed(ex, requestId);
            return;
        }

        if (!claimed)
        {
            return;
        }

        logger.LogProjectionReplayStarted();
        var tally = new ReplayTally();
        try
        {
            await RunClaimedReplayAsync(requestId, tally, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogProjectionReplayFailed(ex);
            Complete(new ReplayCompletion(requestId, ReplayResult.Failed, tally.Replayed, TruncateFailureMessage(ex.Message)));
        }
    }

    /// <summary>
    /// The claimed replay itself. What fails before it reaches its own handling — a scope or a preservation that cannot
    /// be resolved — ends it as failed in the caller.
    /// </summary>
    private async Task RunClaimedReplayAsync(Guid requestId, ReplayTally tally, CancellationToken cancellationToken)
    {
        using var preservationScope = scopeFactory.CreateScope();
        var preservation = preservationScope.ServiceProvider.GetService<IReadModelPreservation>();
        var preserved = false;
        try
        {
            long totalEvents;
            using (var preparing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var renewing = RenewWhilePreparingAsync(preparing.Token);
                try
                {
                    if (preservation is not null)
                    {
                        await preservation.PreserveAsync(requestId, cancellationToken);
                        preserved = true;
                        logger.LogReadModelsPreserved(requestId);
                    }

                    totalEvents = await PrepareAsync(cancellationToken);
                }
                finally
                {
                    await preparing.CancelAsync();
                    await renewing;
                }
            }

            replayState.SetProgress(0, totalEvents);
            await ReplayEventsAsync(tally, totalEvents, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interrupted(requestId, tally);
            return;
        }
        catch (Exception ex)
        {
            logger.LogProjectionReplayFailed(ex);
            var restored = preserved && preservation is not null && await RestoreAsync(preservation, requestId);
            Complete(new ReplayCompletion(requestId, ReplayResult.Failed, tally.Replayed, TruncateFailureMessage(ex.Message))
            {
                ReadModelsRestored = restored,
            });
            return;
        }

        if (!tally.ReachedTheEnd)
        {
            Interrupted(requestId, tally);
            return;
        }

        if (preserved && preservation is not null)
        {
            await DiscardAsync(preservation, requestId);
        }

        logger.LogProjectionReplayCompleted(tally.Replayed);
        Complete(new ReplayCompletion(requestId, ReplayResult.Succeeded, tally.Replayed));
    }

    private async Task<bool> RestoreAsync(IReadModelPreservation preservation, Guid requestId)
    {
        try
        {
            var restored = await preservation.RestoreAsync(requestId, CancellationToken.None);
            if (restored)
            {
                logger.LogReadModelsRestored(requestId);
            }

            return restored;
        }
        catch (Exception ex)
        {
            logger.LogReadModelRestoreFailed(ex);
            return false;
        }
    }

    private async Task DiscardAsync(IReadModelPreservation preservation, Guid requestId)
    {
        try
        {
            await preservation.DiscardAsync(requestId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogPreservedReadModelsNotDiscarded(ex, requestId);
        }
    }

    private void Interrupted(Guid requestId, ReplayTally tally)
    {
        logger.LogProjectionReplayInterrupted(requestId, tally.Replayed);
        Complete(new ReplayCompletion(requestId, ReplayResult.Interrupted, tally.Replayed));
    }

    private void Complete(ReplayCompletion completion)
    {
        try
        {
            replayState.Complete(completion);
        }
        catch (Exception ex)
        {
            logger.LogProjectionReplayOutcomeNotRecorded(ex, completion.RequestId);
        }
    }

    private static string TruncateFailureMessage(string message) =>
        message.Length <= MaxFailureMessageLength
            ? message
            : message[..MaxFailureMessageLength] + "…";

    /// <summary>Empties the records of forgotten tenants and the read models, and counts the events to replay.</summary>
    private async Task<long> PrepareAsync(CancellationToken cancellationToken)
    {
        using (var truncateScope = scopeFactory.CreateScope())
        {
            await ClearForgottenTenantsAsync(truncateScope.ServiceProvider, cancellationToken);
            var viewTruncator = truncateScope.ServiceProvider.GetRequiredService<IProjectionViewTruncator>();
            await viewTruncator.TruncateAllAsync(cancellationToken);
            logger.LogProjectionViewsTruncated();
        }

        return await GetTotalEventCountAsync(cancellationToken);
    }

    /// <summary>
    /// Renews the marking every <see cref="PreparationRenewal"/> until the preparation ends. A renewal that fails is
    /// logged once and tried again on the next tick.
    /// </summary>
    private async Task RenewWhilePreparingAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PreparationRenewal, _timeProvider);
        var failing = false;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    replayState.SetProgress(0, 0);
                }
                catch (Exception renewalFailed) when (renewalFailed is not OperationCanceledException)
                {
                    if (!failing)
                    {
                        failing = true;
                        logger.LogProjectionReplayRenewalFailed(renewalFailed);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The preparation ended; the replay renews the marking with its progress from here.
        }
    }

    /// <summary>
    /// Empties the record of deleted tenants of every projection this host registers that declares
    /// <see cref="IForgetsDeletedTenants"/> — before the views are truncated, so a deletion a stray consumer applies
    /// in between is recorded again rather than lost, and without touching another deployment's projections in a
    /// shared read store. A host without such a projection does not touch the store at all.
    /// </summary>
    private static async Task ClearForgottenTenantsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var forgetting = services.GetServices<IProjection>().OfType<IForgetsDeletedTenants>().ToList();
        if (forgetting.Count == 0 || services.GetService<IForgottenTenantStore>() is not { } store)
        {
            return;
        }

        var handler = services.GetRequiredService<IProjectionHandler>();
        foreach (var projection in forgetting)
        {
            await store.ClearAsync(handler.GetProjectionName(projection), cancellationToken);
        }
    }

    private async Task<long> GetTotalEventCountAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var writeUnitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await writeUnitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = writeUnitOfWork.CreateEventStreamRepository(transaction);

        return await eventStreamRepository.GetMaxSequenceNumberAsync(cancellationToken);
    }

    private async Task ReplayEventsAsync(ReplayTally tally, long totalEvents, CancellationToken cancellationToken)
    {
        long afterSequence = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await ReplayBatchWithRetryAsync(afterSequence, cancellationToken);

            if (batch.Count == 0)
            {
                tally.ReachedTheEnd = true;
                break;
            }

            afterSequence = batch.LastSequence;
            tally.Replayed += batch.Count;

            replayState.SetProgress(tally.Replayed, totalEvents);
            logger.LogProjectionReplayBatchPublished(batch.Count, afterSequence);
        }
    }

    private async Task<ReplayedBatch> ReplayBatchWithRetryAsync(long afterSequence, CancellationToken cancellationToken)
    {
        var attempt = 0;
        return await _batchPipeline.ExecuteAsync(async ct =>
        {
            attempt++;
            try
            {
                return await ReplayBatchAsync(afterSequence, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogProjectionReplayBatchFailed(ex, afterSequence, attempt);
                throw;
            }
        }, cancellationToken);
    }

    private async Task<ReplayedBatch> ReplayBatchAsync(long afterSequence, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var writeUnitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        var eventMapperFactory = scope.ServiceProvider.GetRequiredService<IEventMapperFactory>();
        var sessionContextProvider = scope.ServiceProvider.GetRequiredService<ISessionContextProvider>();
        var projectionManager = scope.ServiceProvider.GetRequiredService<IProjectionManager>();

        await using var transaction = await writeUnitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = writeUnitOfWork.CreateEventStreamRepository(transaction);

        var entries = await eventStreamRepository.GetManyAfterSequenceInStreamOrderAsync(
            afterSequence, _options.BatchSize, cancellationToken);

        if (entries.Count == 0)
        {
            return ReplayedBatch.Empty;
        }

        var relevance = projectionManager is ProjectionManager ? ProjectionEventRelevance.Of(scope.ServiceProvider) : null;
        foreach (var entry in entries)
        {
            var sessionContext = new SessionContext(
                entry.CorrelationId ?? Guid.CreateVersion7().ToString("N"),
                entry.CausationId,
                null,
                entry.ActorTenantId,
                entry.ActorUserId,
                entry.TenantId,
                entry.UserId);
            sessionContextProvider.Set(sessionContext);

            var events = relevance is null
                ? await eventMapperFactory.MapToEventsAsync([entry], cancellationToken)
                : await eventMapperFactory.MapToEventsAsync([entry], relevance, cancellationToken);
            await projectionManager.HandleAsync(events, cancellationToken);
        }

        return new ReplayedBatch(entries.Count, entries.Max(entry => entry.SequenceNumber));
    }

    /// <summary>
    /// How many events the running replay has applied, and whether it reached the end of the store — readable after it
    /// ends however it ends.
    /// </summary>
    private sealed class ReplayTally
    {
        public long Replayed { get; set; }

        public bool ReachedTheEnd { get; set; }
    }

    private sealed record ReplayedBatch(int Count, long LastSequence)
    {
        public static readonly ReplayedBatch Empty = new(0, 0);
    }
}
