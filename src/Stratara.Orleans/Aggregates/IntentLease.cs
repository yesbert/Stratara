using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Outbox;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Keeps a running command from becoming due again: renews its hand-over at half the grace until the
/// execution ends, so however long the handler runs, the drain does not hand it over a second time. A
/// renewal that fails is skipped; the next one tries again.
/// </summary>
/// <remarks>
/// A command handed over moments after it was recorded is not renewed when execution starts: its record
/// time holds it until the first renewal. The renewal at the start is one statement per command inside
/// the aggregate's turn, which cost the durable-intent shape 13 % of its throughput on one aggregate. A
/// command that waited in the turn longer, or one the drain resumed, is renewed at the start.
/// </remarks>
internal sealed class IntentLease : IAsyncDisposable
{
    private const int GuidVersionWithTimestamp = 7;

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

    /// <summary>Renews the hand-over now unless its record time still holds it, and keeps renewing it until the lease is disposed.</summary>
    public static async Task<IntentLease> StartAsync(IServiceProvider services, Guid intentId)
    {
        var intents = services.GetRequiredService<ICommandIntentStore>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var grace = services.GetRequiredService<IOptions<OrleansDispatchOptions>>().Value.IntentGrace;

        var now = timeProvider.GetUtcNow();
        if (RenewsAtStart(intentId, now, grace))
        {
            await intents.RenewAsync(intentId, now, CancellationToken.None);
        }

        var lease = new IntentLease(intents, timeProvider, intentId);
        lease._renewing = lease.RenewUntilStoppedAsync(grace / 2);
        return lease;
    }

    /// <summary>
    /// Whether the hand-over has to be renewed when execution starts. The record is stamped after its id is
    /// created, so a time-ordered id no older than a quarter of the grace means the record stays out of the
    /// drain until the first renewal at half the grace, with a quarter of the grace to spare. Any other id
    /// — an older one, which is also every resumed command, or one that carries no time — is renewed.
    /// </summary>
    internal static bool RenewsAtStart(Guid intentId, DateTimeOffset now, TimeSpan grace)
    {
        if (intentId.Version != GuidVersionWithTimestamp)
        {
            return true;
        }

        Span<byte> bytes = stackalloc byte[16];
        intentId.TryWriteBytes(bytes, bigEndian: true, out _);
        var unixMilliseconds = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24) | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
        return now - DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds) >= grace / 4;
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
