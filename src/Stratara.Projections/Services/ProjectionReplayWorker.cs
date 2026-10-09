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
/// event, batched
/// by <see cref="ProjectionOptions.BatchSize"/>, each stream in version order and the streams interleaved as
/// their sequence numbers interleave them.
/// </summary>
/// <remarks>
/// A save does not number its entries in version order, so a batch is read through
/// <see cref="IEventStreamRepository.GetManyAfterSequenceInStreamOrderAsync"/>, which may return more entries than
/// the batch size and whose last entry need not carry its highest sequence number; the next batch starts after
/// the highest. Each batch is processed in a fresh DI scope so the unit-of-work and session context lifecycle matches
/// what real-time projection dispatch sees. Each batch — reading it and applying it — runs under the
/// <see cref="ResilienceNames.ProjectionReplayBatch"/> policy: a failed attempt disposes its scope and the
/// batch is applied again from its first entry in a new one, so a passing failure such as a read-store
/// timeout does not end the replay. Once the attempts are exhausted the failure ends the replay as an
/// unretried one would. A request runs only if this host claims it through
/// <see cref="IProjectionReplayState.TryActivate"/>, and the replay ends in one
/// <see cref="IProjectionReplayState.Complete"/> — succeeded, failed with its message truncated to 500 characters,
/// or interrupted by the host stopping — so consumer-side dashboards can read its outcome after it ended.
/// </remarks>
internal sealed class ProjectionReplayWorker(
    ILogger<ProjectionReplayWorker> logger,
    IServiceScopeFactory scopeFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<ProjectionOptions> options) : BackgroundService
{
    private const int MaxFailureMessageLength = 500;

    private readonly ProjectionOptions _options = options.Value;
    private readonly ResiliencePipeline _batchPipeline = pipelineProvider.GetPipeline(ResilienceNames.ProjectionReplayBatch);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await replayState.SubscribeToReplayRequestAsync(
            requestId => RunRequestedReplayAsync(requestId, stoppingToken), stoppingToken);
    }

    /// <summary>
    /// Runs the replay a request asks for, if this host claims the request: a request every host receives, or one
    /// that arrives while another replay is active, starts nothing here. The replay ends in exactly one completion —
    /// succeeded, failed with its message, or interrupted because the host stops — which keeps its outcome.
    /// </summary>
    private async Task RunRequestedReplayAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (!replayState.TryActivate(requestId))
        {
            return;
        }

        logger.LogProjectionReplayStarted();
        var tally = new ReplayTally();
        try
        {
            await RunReplayAsync(tally, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interrupted(tally);
            return;
        }
        catch (Exception ex)
        {
            logger.LogProjectionReplayFailed(ex);
            replayState.Complete(new ReplayCompletion(ReplayResult.Failed, tally.Replayed, TruncateFailureMessage(ex.Message)));
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Interrupted(tally);
            return;
        }

        logger.LogProjectionReplayCompleted(tally.Replayed);
        replayState.Complete(new ReplayCompletion(ReplayResult.Succeeded, tally.Replayed));
    }

    private void Interrupted(ReplayTally tally)
    {
        logger.LogProjectionReplayInterrupted(tally.Replayed);
        replayState.Complete(new ReplayCompletion(ReplayResult.Interrupted, tally.Replayed));
    }

    private static string TruncateFailureMessage(string message) =>
        message.Length <= MaxFailureMessageLength
            ? message
            : message[..MaxFailureMessageLength] + "…";

    private async Task RunReplayAsync(ReplayTally tally, CancellationToken cancellationToken)
    {
        using (var truncateScope = scopeFactory.CreateScope())
        {
            await ClearForgottenTenantsAsync(truncateScope.ServiceProvider, cancellationToken);
            var viewTruncator = truncateScope.ServiceProvider.GetRequiredService<IProjectionViewTruncator>();
            await viewTruncator.TruncateAllAsync(cancellationToken);
            logger.LogProjectionViewsTruncated();
        }

        var totalEvents = await GetTotalEventCountAsync(cancellationToken);
        replayState.SetProgress(0, totalEvents);

        await ReplayEventsAsync(tally, totalEvents, cancellationToken);
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

    /// <summary>How many events the running replay has applied, readable after it ends however it ends.</summary>
    private sealed class ReplayTally
    {
        public long Replayed { get; set; }
    }

    private sealed record ReplayedBatch(int Count, long LastSequence)
    {
        public static readonly ReplayedBatch Empty = new(0, 0);
    }
}
