using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Messaging;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Outbox.RabbitMQ.Messaging;

/// <summary>
/// Lets the subscriptions that stopped with the host finish stopping before the host counts as stopped, so the handlers
/// they are still running settle their messages while the services those handlers use are still there — not when the
/// container is disposed, and not never for a host that is stopped without being disposed. Runs after every hosted
/// service has stopped, within the host's shutdown timeout.
/// </summary>
internal sealed class RabbitMqSubscriptionsDrain(IMessageBus bus, ILogger<RabbitMqSubscriptionsDrain> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        // Another transport replaced the bus; its own drain covers it.
        if (bus is not RabbitMqBus rabbit)
        {
            return;
        }

        try
        {
            await rabbit.WhenSubscriptionsStoppedAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogSubscriptionCleanupFailed("subscriptions", ex);
        }
    }
}
