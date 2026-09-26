using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Outbox.RabbitMQ.Messaging;

/// <summary>
/// The subscriptions of the RabbitMQ bus that are stopping, and how long they may wait for their running handlers.
/// As a hosted service it lets the subscriptions that stop with the host finish stopping before the host counts as
/// stopped, so the handlers they are still running settle their messages while the services those handlers use are
/// still there — not when the container is disposed, and not never for a host that is stopped without being disposed.
/// </summary>
/// <remarks>
/// <para>
/// From the moment the application starts stopping until the host has stopped, a subscription that starts stopping
/// waits for its handlers until the host's shutdown timeout runs out; any other time — or a bus used without a host —
/// for the transport's own bound. The bus reports its stopping subscriptions here instead of this service holding the
/// bus, so the service needs nothing the bus needs: a host that replaced or decorated the message bus starts as before.
/// </para>
/// <para>
/// A host whose shutdown timeout is infinite waits as long as a running handler does.
/// </para>
/// </remarks>
internal sealed class RabbitMqSubscriptionStops(
    ILogger<RabbitMqSubscriptionStops> logger,
    IHostApplicationLifetime? lifetime = null) : IHostedLifecycleService
{
    private readonly ConcurrentBag<Task> _stops = new();

    // Set while the host stops; cancelled when its shutdown timeout runs out. Never disposed: it carries no timer, and a
    // subscription that started stopping may still link to it.
    private CancellationTokenSource? _hostStop;
    private CancellationTokenRegistration _applicationStopping;
    private CancellationTokenRegistration _shutdownElapsed;

    /// <summary>Records a subscription's stop, so the host and the bus's disposal can wait for it.</summary>
    public void Add(Task stop) => _stops.Add(stop);

    /// <summary>Completes when every subscription that was stopped has finished stopping.</summary>
    public Task WhenAllStoppedAsync() => Task.WhenAll(_stops);

    /// <summary>
    /// The deadline for a subscription that starts stopping now: the host's shutdown timeout while the host stops,
    /// otherwise <paramref name="standalone"/> from now.
    /// </summary>
    public CancellationTokenSource Deadline(TimeSpan standalone) =>
        Volatile.Read(ref _hostStop) is { } hostStop
            ? CancellationTokenSource.CreateLinkedTokenSource(hostStop.Token)
            : new CancellationTokenSource(standalone);

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        // A subscription tied to the application's stopping token stops before the host hands over its shutdown
        // timeout, and waits under it all the same.
        if (lifetime is not null)
        {
            _applicationStopping = lifetime.ApplicationStopping.Register(HostStopping);
        }

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        HostStopping();
        _shutdownElapsed = cancellationToken.Register(ShutdownElapsed);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WhenAllStoppedAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Said here as well as through the registration: this continuation can run inside the token's own
            // cancellation, before the registration's callback has, and disposing the registration below would then
            // drop that callback — leaving the subscriptions waiting for their handlers after the host gave up.
            ShutdownElapsed();
            logger.LogSubscriptionCleanupFailed("subscriptions", ex);
        }
        finally
        {
            await _shutdownElapsed.DisposeAsync();
            await _applicationStopping.DisposeAsync();

            // The host has stopped: a subscription stopped later waits for the transport's own bound again.
            Interlocked.Exchange(ref _hostStop, null)?.Cancel();
        }
    }

    private void HostStopping()
    {
        if (Volatile.Read(ref _hostStop) is null)
        {
            Interlocked.CompareExchange(ref _hostStop, new CancellationTokenSource(), null);
        }
    }

    private void ShutdownElapsed() => Volatile.Read(ref _hostStop)?.Cancel();
}
