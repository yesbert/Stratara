using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Hosted service that periodically (every 15 seconds) drives the
/// <see cref="IEventStreamHashService"/> to hash newly-committed events and the
/// <see cref="IEventChainService"/> to write tamper-evident anchors.
/// </summary>
/// <remarks>
/// Each iteration is wrapped in a try/catch so transient failures do not stop the worker; on
/// failure, the worker delays and retries on the next tick. Only one host across the deployment
/// should run this worker.
/// </remarks>
internal sealed class EventStreamHashWorker(ILogger<EventStreamHashWorker> logger, IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan DelayInSeconds = TimeSpan.FromSeconds(15);

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogEventStreamHashWorkerStarted();
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogEventStreamHashWorkerStopped();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var hashService = scope.ServiceProvider.GetRequiredService<IEventStreamHashService>();
                var anchorService = scope.ServiceProvider.GetRequiredService<IEventChainService>();

                await hashService.HashEventsAsync(stoppingToken);
                await anchorService.AddAnchorIfNeededAsync(stoppingToken);

                await Task.Delay(DelayInSeconds, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogEventStreamHashWorkerOperationCanceled();
            }
            catch (Exception ex)
            {
                logger.LogEventStreamHashWorkerFailed(ex);
                await Task.Delay(DelayInSeconds, stoppingToken);
            }
        }
    }
}