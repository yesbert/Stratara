using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.BackgroundTasks;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Infrastructure.BackgroundTasks;

/// <summary>
/// Hosted service that drains an <see cref="IBackgroundTaskQueue"/> with one worker loop per
/// processor, executing each item inside a fresh DI scope and updating its
/// <see cref="BackgroundTaskStatus"/>.
/// </summary>
/// <remarks>
/// Exceptions inside a work item are caught, logged, and the task is marked <see cref="BackgroundTaskStatus.Failed"/>.
/// The worker keeps draining the queue; one failing item does not stop the host.
/// </remarks>
internal sealed class QueuedHostedService(
    ILogger<QueuedHostedService> logger,
    IServiceProvider serviceProvider,
    IBackgroundTaskQueue taskQueue) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogQueuedHostedServiceStarted();
        var degreeOfParallelism = Environment.ProcessorCount;
        var workers = new Task[degreeOfParallelism];
        for (var i = 0; i < degreeOfParallelism; i++)
        {
            workers[i] = BackgroundProcessing(stoppingToken);
        }

        await Task.WhenAll(workers);

    }

    private async Task BackgroundProcessing(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var workItem = await taskQueue.DequeueAsync(stoppingToken);
            using var scope = serviceProvider.CreateScope();

            try
            {
                taskQueue.UpdateTaskStatus(workItem.taskId, BackgroundTaskStatus.Running);
                await workItem.task(scope.ServiceProvider, stoppingToken);
                taskQueue.UpdateTaskStatus(workItem.taskId, BackgroundTaskStatus.Completed);
                logger.LogJobSuccessfulExecuted(workItem.task.Target?.GetType().Name ?? "Unknown");
            }
            catch (Exception ex)
            {
                taskQueue.UpdateTaskStatus(workItem.taskId, BackgroundTaskStatus.Failed, ex.Message);
                logger.LogJobFailedExecuted(ex);
            }
        }
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogQueuedHostedServiceStopped();
        await base.StopAsync(cancellationToken);
    }
}
