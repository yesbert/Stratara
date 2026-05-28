using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Contracts.Session;
using Stratara.Projections.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Projections.Services;

/// <summary>
/// Background service that replays the full event stream against all projections on demand. Triggered via
/// <see cref="IProjectionReplayState"/>; truncates all projection views and re-applies every event in
/// sequence-number order, batched by <see cref="ProjectionOptions.BatchSize"/>.
/// </summary>
/// <remarks>
/// Each batch is processed in a fresh DI scope so the unit-of-work and session context lifecycle matches
/// what real-time projection dispatch sees. Failures truncate the message to 500 characters and surface
/// via <see cref="IProjectionReplayState.SetFailed"/> so consumer-side dashboards can display the cause.
/// </remarks>
internal sealed class ProjectionReplayWorker(
    ILogger<ProjectionReplayWorker> logger,
    IServiceScopeFactory scopeFactory,
    IProjectionReplayState replayState,
    IOptions<ProjectionOptions> options) : BackgroundService
{
    private const int MaxFailureMessageLength = 500;

    private readonly ProjectionOptions _options = options.Value;

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
                break;
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

            afterSequence = entries[^1].SequenceNumber;
            totalReplayed += entries.Count;

            replayState.SetProgress(totalReplayed, totalEvents);
            logger.LogProjectionReplayBatchPublished(entries.Count, afterSequence);
        }

        return totalReplayed;
    }
}
