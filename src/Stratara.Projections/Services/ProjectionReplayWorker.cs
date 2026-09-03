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
/// <see cref="IProjectionReplayState"/>; truncates all projection views and re-applies every event in
/// sequence-number order, batched by <see cref="ProjectionOptions.BatchSize"/>.
/// </summary>
/// <remarks>
/// Each batch is processed in a fresh DI scope so the unit-of-work and session context lifecycle matches
/// what real-time projection dispatch sees. Each batch — reading it and applying it — runs under the
/// <see cref="ResilienceNames.ProjectionReplayBatch"/> policy: a failed attempt disposes its scope and the
/// batch is applied again from its first entry in a new one, so a passing failure such as a read-store
/// timeout does not end the replay. Once the attempts are exhausted the failure ends the replay as an
/// unretried one would. Failures truncate the message to 500 characters and surface via
/// <see cref="IProjectionReplayState.SetFailed"/> so consumer-side dashboards can display the cause.
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
        await replayState.SubscribeToReplayRequestAsync(async () =>
        {
            try
            {
                await RunReplayAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown — let the worker exit cleanly without surfacing as a replay failure.
            }
            catch (Exception ex)
            {
                logger.LogProjectionReplayFailed(ex);
                replayState.SetFailed(TruncateFailureMessage(ex.Message));
            }
        }, stoppingToken);
    }

    private static string TruncateFailureMessage(string message) =>
        message.Length <= MaxFailureMessageLength
            ? message
            : message[..MaxFailureMessageLength] + "…";

    private async Task RunReplayAsync(CancellationToken cancellationToken)
    {
        replayState.Activate();
        logger.LogProjectionReplayStarted();

        try
        {
            using var truncateScope = scopeFactory.CreateScope();
            var viewTruncator = truncateScope.ServiceProvider.GetRequiredService<IProjectionViewTruncator>();
            await viewTruncator.TruncateAllAsync(cancellationToken);
            logger.LogProjectionViewsTruncated();

            var totalEvents = await GetTotalEventCountAsync(cancellationToken);
            replayState.SetProgress(0, totalEvents);

            var totalReplayed = await ReplayEventsAsync(totalEvents, cancellationToken);

            logger.LogProjectionReplayCompleted(totalReplayed);
        }
        finally
        {
            replayState.Deactivate();
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

    private async Task<long> ReplayEventsAsync(long totalEvents, CancellationToken cancellationToken)
    {
        long afterSequence = 0;
        long totalReplayed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await ReplayBatchWithRetryAsync(afterSequence, cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            afterSequence = batch.LastSequence;
            totalReplayed += batch.Count;

            replayState.SetProgress(totalReplayed, totalEvents);
            logger.LogProjectionReplayBatchPublished(batch.Count, afterSequence);
        }

        return totalReplayed;
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

        var entries = await eventStreamRepository.GetManyAfterSequenceAsync(
            afterSequence, _options.BatchSize, cancellationToken);

        if (entries.Count == 0)
        {
            return ReplayedBatch.Empty;
        }

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

            var events = await eventMapperFactory.MapToEventsAsync([entry], cancellationToken);
            await projectionManager.HandleAsync(events, cancellationToken);
        }

        return new ReplayedBatch(entries.Count, entries[^1].SequenceNumber);
    }

    private sealed record ReplayedBatch(int Count, long LastSequence)
    {
        public static readonly ReplayedBatch Empty = new(0, 0);
    }
}
