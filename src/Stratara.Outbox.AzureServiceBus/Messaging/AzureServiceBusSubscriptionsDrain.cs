using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Outbox.AzureServiceBus.Diagnostics.Extensions;

namespace Stratara.Outbox.AzureServiceBus.Messaging;

/// <summary>
/// Lets the subscriptions that stop with the host finish stopping before the host counts as stopped, so the handlers
/// they are still running settle their messages while the services those handlers use are still there — not when the
/// container is disposed, and not never for a host that is stopped without being disposed. Before anything stops it
/// hands the bus the host's shutdown timeout, which then bounds how long a stopping subscription waits for its
/// handler; after every hosted service has stopped it waits for the subscriptions, within the same timeout.
/// </summary>
/// <remarks>
/// Takes the Service Bus bus itself rather than <c>IMessageBus</c>, so a decorator around the message bus does not
/// hide it.
/// </remarks>
internal sealed class AzureServiceBusSubscriptionsDrain(AzureServiceBusBus bus, ILogger<AzureServiceBusSubscriptionsDrain> logger) : IHostedLifecycleService
{
    private CancellationTokenRegistration _settling;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        _settling = bus.SettleWithin(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await bus.WhenSubscriptionsStoppedAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Said here as well as through the registration: this continuation can run inside the token's own
            // cancellation, before the registration's callback has, and disposing the registration below would then
            // drop that callback — leaving the subscriptions waiting for their handlers after the host gave up.
            bus.ShutdownElapsed();
            logger.LogSubscriptionCleanupFailed("subscriptions", ex);
        }
        finally
        {
            await _settling.DisposeAsync();
        }
    }
}
