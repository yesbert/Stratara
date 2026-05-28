using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Contracts.Messages;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Outbox.RabbitMQ.Outbox;

/// <summary>
/// Hosted background service that drains the outbox table on a fixed polling interval.
/// Pulls unpublished <c>CommandEnvelope</c>s and <c>EventBundle</c>s in batches and asks the
/// corresponding dispatcher (<see cref="CommandOutboxDispatcher"/> /
/// <see cref="EventBundleOutboxDispatcher"/>) to attempt republishing them.
/// </summary>
/// <remarks>
/// Concurrency: each polling cycle is guarded by <see cref="IOutboxLock"/>. The default
/// registration (<see cref="NullOutboxLock"/>) is a no-op that preserves the historical
/// single-instance assumption; consumers that run multiple worker replicas opt in to a
/// distributed lock such as <c>RedisOutboxLock</c> via <c>AddRedisOutboxLock</c>. When the
/// lock is not granted the cycle is skipped and re-attempted at the next poll interval.
/// </remarks>
internal sealed class OutboxWorker(
    ILogger<OutboxWorker> logger,
    IServiceScopeFactory scopeFactory,
    IOutboxLock outboxLock,
    IOptions<OutboxOptions> options) : BackgroundService
{
    private readonly int _batchSize = options.Value.BatchSize;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds);
    private readonly TimeSpan _lockLease = TimeSpan.FromSeconds(options.Value.LockLeaseSeconds);

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogOutboxWorkerStarted();
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogOutboxWorkerStopped();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryDrainOnceAsync(stoppingToken);
                await Task.Delay(_pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogOutboxWorkerOperationCanceled();
            }
            catch (Exception ex)
            {
                logger.LogOutboxFailed(ex);
                await Task.Delay(_pollingInterval, stoppingToken);
            }
        }
    }

    private async Task TryDrainOnceAsync(CancellationToken stoppingToken)
    {
        await using var handle = await outboxLock.TryAcquireAsync(_lockLease, stoppingToken);
        if (handle is null)
        {
            logger.LogOutboxLockNotAcquired();
            return;
        }

        await HandleUnpublishedCommandsAsync(stoppingToken);
        await HandleUnpublishedEventsAsync(stoppingToken);
    }

    private async Task HandleUnpublishedCommandsAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync(stoppingToken);
        var repository = unitOfWork.CreateOutboxRepository(transaction);

        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();

        var outboxEntries = await repository.GetManyAsync<CommandEnvelope>(_batchSize, stoppingToken);

        while (outboxEntries.Count > 0 && !stoppingToken.IsCancellationRequested)
        {
            await dispatcher.EnqueueOutboxEntriesAsync(outboxEntries, stoppingToken);
            outboxEntries = await repository.GetManyAsync<CommandEnvelope>(_batchSize, stoppingToken);
        }
    }

    private async Task HandleUnpublishedEventsAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync(stoppingToken);
        var repository = unitOfWork.CreateOutboxRepository(transaction);

        var dispatcher = scope.ServiceProvider.GetRequiredService<IEventBundleOutboxDispatcher>();

        var outboxEntries = await repository.GetManyAsync<EventBundle>(_batchSize, stoppingToken);

        while (outboxEntries.Count > 0 && !stoppingToken.IsCancellationRequested)
        {
            await dispatcher.EnqueueOutboxEntriesAsync(outboxEntries, stoppingToken);
            outboxEntries = await repository.GetManyAsync<EventBundle>(_batchSize, stoppingToken);
        }
    }
}
