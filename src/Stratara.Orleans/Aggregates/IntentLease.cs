using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Outbox;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Keeps a running command from becoming due again: renews its hand-over when execution starts and at
/// half the grace until the execution ends, so however long the handler runs, the drain does not hand
/// it over a second time. A renewal that fails is skipped; the next one tries again.
/// </summary>
internal sealed class IntentLease : IAsyncDisposable
{
    private readonly ICommandIntentStore _intents;
    private readonly TimeProvider _timeProvider;
    private readonly Guid _intentId;
    private readonly CancellationTokenSource _stop = new();
    private Task _renewing = Task.CompletedTask;

    private IntentLease(ICommandIntentStore intents, TimeProvider timeProvider, Guid intentId)
    {
        _intents = intents;
        _timeProvider = timeProvider;
        _intentId = intentId;
    }

    /// <summary>Renews the hand-over now and keeps renewing it until the lease is disposed.</summary>
    public static async Task<IntentLease> StartAsync(IServiceProvider services, Guid intentId)
    {
        var intents = services.GetRequiredService<ICommandIntentStore>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var period = services.GetRequiredService<IOptions<OrleansDispatchOptions>>().Value.IntentGrace / 2;

        await intents.RenewAsync(intentId, timeProvider.GetUtcNow(), CancellationToken.None);
        var lease = new IntentLease(intents, timeProvider, intentId);
        lease._renewing = lease.RenewUntilStoppedAsync(period);
        return lease;
    }

    /// <summary>Records the failure of this attempt with the command; a failure to record it does not hide the original one.</summary>
    public async Task RecordFailureAsync(Exception failure)
    {
        try
        {
            await _intents.RecordFailureAsync(_intentId, IntentFailure.Describe(failure), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _renewing;
        _stop.Dispose();
    }

    private async Task RenewUntilStoppedAsync(TimeSpan period)
    {
        using var timer = new PeriodicTimer(period, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try
                {
                    await _intents.RenewAsync(_intentId, _timeProvider.GetUtcNow(), _stop.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _ = ex;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }
}
