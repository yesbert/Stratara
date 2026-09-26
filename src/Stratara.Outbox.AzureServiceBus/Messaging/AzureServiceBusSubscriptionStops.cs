using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Outbox.AzureServiceBus.Diagnostics.Extensions;

namespace Stratara.Outbox.AzureServiceBus.Messaging;

/// <summary>
/// The subscriptions of the Service Bus bus that are stopping, and how long they may wait for their running handlers.
/// As a hosted service it lets the subscriptions that stop with the host finish stopping before the host counts as
/// stopped, so the handlers they are still running settle their messages while the services those handlers use are
/// still there — not when the container is disposed, and not never for a host that is stopped without being disposed.
/// </summary>
/// <remarks>
/// <para>
/// From the moment the application starts stopping until the host has stopped, a subscription that starts stopping
/// waits for its handlers until the host's shutdown timeout runs out; any other time — or a bus used without a host —
/// for <see cref="StandaloneSettleTimeout"/>. The bus reports its stopping subscriptions here instead of this service
/// holding the bus, so the service needs nothing the bus needs: a host that replaced or decorated the message bus starts
/// as before.
/// </para>
/// <para>
/// A host whose shutdown timeout is infinite waits as long as a running handler does. A host whose application was
/// told to stop but which is disposed without being stopped waits <see cref="StandaloneSettleTimeout"/> from the bus's
/// disposal.
/// </para>
/// </remarks>
internal sealed class AzureServiceBusSubscriptionStops(
    ILogger<AzureServiceBusSubscriptionStops> logger,
    IHostApplicationLifetime? lifetime = null) : IHostedLifecycleService
{
    /// <summary>
    /// How long a subscription that stops outside a host's stop — its own token was cancelled — waits for the handlers
    /// it is running to settle their messages. Within the host's default shutdown timeout.
    /// </summary>
    internal static readonly TimeSpan StandaloneSettleTimeout = TimeSpan.FromSeconds(20);

    private readonly ConcurrentBag<Task> _stops = new();

    // Set while the host stops; cancelled when its shutdown timeout runs out, and at the latest the standalone bound
    // after the host has stopped or the bus is disposed. A subscription that started stopping may still link to it.
    private CancellationTokenSource? _hostStop;
    private volatile bool _hostStopped;
    private CancellationTokenRegistration _shutdownElapsed;

    /// <summary>Records a subscription's stop, so the host and the bus's disposal can wait for it.</summary>
    public void Add(Task stop) => _stops.Add(stop);

    /// <summary>Completes when every subscription that was stopped has finished stopping.</summary>
    public Task WhenAllStoppedAsync() => Task.WhenAll(_stops);

    /// <summary>
    /// The deadline for a subscription that starts stopping now: the host's shutdown timeout while the host stops,
    /// otherwise <see cref="StandaloneSettleTimeout"/> from now.
    /// </summary>
    public CancellationTokenSource Deadline() =>
        HostStop() is { } hostStop
            ? CancellationTokenSource.CreateLinkedTokenSource(hostStop.Token)
            : new CancellationTokenSource(StandaloneSettleTimeout);

    /// <summary>
    /// The bus is being disposed: a wait still tied to a host that never stopped — its application was told to stop,
    /// and it was disposed without being stopped — runs out <see cref="StandaloneSettleTimeout"/> from now.
    /// </summary>
    public void BusDisposing() => Volatile.Read(ref _hostStop)?.CancelAfter(StandaloneSettleTimeout);

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        _shutdownElapsed = cancellationToken.Register(ShutdownElapsed, HostStop(starting: true));
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
            Volatile.Read(ref _hostStop)?.Cancel();
            logger.LogSubscriptionCleanupFailed("subscriptions", ex);
        }
        finally
        {
            await _shutdownElapsed.DisposeAsync();

            // The host has stopped: a subscription stopped from now on waits for the standalone bound, and one that
            // took the host's deadline just now still gets that long.
            _hostStopped = true;
            Interlocked.Exchange(ref _hostStop, null)?.CancelAfter(StandaloneSettleTimeout);
        }
    }

    /// <summary>
    /// The host's stop while it lasts. It starts with <see cref="StoppingAsync"/> or, earlier, when the application's
    /// stopping token is cancelled — read here rather than registered on, because that token is cancelled before any of
    /// its callbacks run, and a subscription tied to it stops in one of those, before a callback registered earlier.
    /// </summary>
    private CancellationTokenSource? HostStop(bool starting = false)
    {
        if (Volatile.Read(ref _hostStop) is { } hostStop)
        {
            return hostStop;
        }

        if (_hostStopped || (!starting && lifetime is not { ApplicationStopping.IsCancellationRequested: true }))
        {
            return null;
        }

        Interlocked.CompareExchange(ref _hostStop, new CancellationTokenSource(), null);
        return Volatile.Read(ref _hostStop);
    }

    private static void ShutdownElapsed(object? hostStop) => ((CancellationTokenSource?)hostStop)?.Cancel();
}
