using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Outbox;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// One renewal loop for every command a silo holds — running in an aggregate's order, in a runner, or in the heavy pool,
/// waiting or not. A lease registers its command and unregisters it when it ends; every third of the grace the loop
/// renews everything registered in batches of <see cref="BatchSize"/>, one store call per batch, so holding a thousand
/// commands costs what holding ten does. A batch that fails is logged once and renewed again at the next tick; the
/// grace spans three ticks, so one failed tick does not let a hand-over lapse.
/// </summary>
/// <remarks>
/// The loop runs from a timer of its own, off every grain's scheduler, so a handler that computes without awaiting is
/// renewed all the same. One renewal runs at a time; a tick that finds one running does nothing. The timer starts with
/// the first command held and stops only when the container disposes the loop, not when the host stops: the host stops
/// its services before the silo, and the silo runs its handlers through its deactivation budget after that, so a loop
/// that stopped with the host would let a running command's record lapse during a stop. A command held twice on one
/// silo — two hand-overs of it held by two activations — stays renewed until both release it.
/// </remarks>
internal sealed class IntentRenewals : IAsyncDisposable
{
    /// <summary>How many commands one store call renews.</summary>
    public const int BatchSize = 1_000;

    private readonly ConcurrentDictionary<Guid, int> _held = new();
    private readonly object _gate = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly TimeSpan _period;
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private volatile Task _inFlight = Task.CompletedTask;
    private int _renewing;
    private int _disposed;

    public IntentRenewals(IServiceScopeFactory scopes, IOptions<OrleansDispatchOptions> options, TimeProvider timeProvider, ILogger<IntentRenewals> logger)
    {
        _scopes = scopes;
        _timeProvider = timeProvider;
        _logger = logger;
        _period = options.Value.IntentGrace / 3;
    }

    /// <summary>How many commands are registered for renewal now.</summary>
    public int Count => _held.Count;

    /// <summary>Keeps renewing <paramref name="intentId"/> until every hold of it is released; starts the loop on first use.</summary>
    public void Hold(Guid intentId)
    {
        _held.AddOrUpdate(intentId, 1, static (_, holds) => holds + 1);
        if (_timer is null)
        {
            lock (_gate)
            {
                if (_timer is null && Volatile.Read(ref _disposed) == 0)
                {
                    _timer = _timeProvider.CreateTimer(static state => ((IntentRenewals)state!).OnTick(), this, _period, _period);
                }
            }
        }
    }

    /// <summary>Releases one hold of <paramref name="intentId"/>; the last release stops its renewal, though a batch already sent may still renew it once.</summary>
    public void Release(Guid intentId)
    {
        while (_held.TryGetValue(intentId, out var holds))
        {
            if (holds <= 1 ? _held.TryRemove(new KeyValuePair<Guid, int>(intentId, holds)) : _held.TryUpdate(intentId, holds - 1, holds))
            {
                return;
            }
        }
    }

    /// <summary>Stops the loop, once the container is disposed; a second call does nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync();
        if (_timer is { } timer)
        {
            await timer.DisposeAsync();
        }

        await _inFlight;
        _stop.Dispose();
    }

    /// <summary>Renews everything registered now, in batches; what a tick does, callable from a test.</summary>
    internal async Task RenewAllAsync(CancellationToken cancellationToken)
    {
        var held = _held.Keys.ToList();
        if (held.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        await using var scope = _scopes.CreateAsyncScope();
        var intents = scope.ServiceProvider.GetRequiredService<ICommandIntentStore>();
        foreach (var batch in held.Chunk(BatchSize))
        {
            try
            {
                await intents.RenewManyAsync(batch, now, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogIntentBatchRenewalFailed(ex, batch.Length);
            }
        }
    }

    private void OnTick()
    {
        if (_stop.IsCancellationRequested || Interlocked.CompareExchange(ref _renewing, 1, 0) != 0)
        {
            return;
        }

        _inFlight = RenewOnTickAsync();
    }

    private async Task RenewOnTickAsync()
    {
        try
        {
            await RenewAllAsync(_stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The loop stopped; nothing is left to renew.
        }
        catch (Exception ex)
        {
            _logger.LogIntentBatchRenewalFailed(ex, _held.Count);
        }
        finally
        {
            Volatile.Write(ref _renewing, 0);
        }
    }
}
