using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Outbox;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Keeps a running command from becoming due again: renews its hand-over every third of the grace until the
/// execution ends, so however long the handler runs, the drain does not hand it over a second time, and a single
/// renewal that fails does not let the hand-over lapse. A renewal that fails is logged and skipped; the next one
/// tries again. The renewals run from a timer of their own, off the scheduler the lease was started on: a handler
/// that blocks its activation's scheduler — computing without awaiting — is renewed all the same.
/// </summary>
/// <remarks>
/// A command handed over moments after it was recorded is not renewed when execution starts: its record time holds
/// it past the first two renewals. The renewal at the start is one statement per command inside the aggregate's
/// turn, which cost the durable-intent shape 13 % of its throughput on one aggregate. A command that waited in the
/// turn longer is renewed at the start; one the drain resumed is taken over at the start by a renewal from the claim's
/// stamp, which also decides whether it runs at all.
/// </remarks>
internal sealed class IntentLease : IAsyncDisposable
{
    private const int GuidVersionWithTimestamp = 7;

    private readonly ICommandIntentStore _intents;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Guid _intentId;
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private volatile Task _inFlight = Task.CompletedTask;
    private int _renewing;

    private IntentLease(ICommandIntentStore intents, TimeProvider timeProvider, ILogger logger, Guid intentId)
    {
        _intents = intents;
        _timeProvider = timeProvider;
        _logger = logger;
        _intentId = intentId;
    }

    /// <summary>
    /// Takes the hand-over and keeps renewing it until the lease is disposed. A resumed hand-over — one that carries
    /// <paramref name="claimedAt"/> — is taken only if its record still carries that stamp; one that finds the stamp
    /// moved or the record gone is dropped, logged, and answered with <see langword="null"/>, and its handler does not
    /// run. Any other hand-over is renewed now unless its record time still holds it, and is dropped the same way when
    /// that renewal finds the record gone or kept.
    /// </summary>
    /// <returns>The lease, or <see langword="null"/> when the hand-over was dropped.</returns>
    /// <remarks>
    /// Only a renewal that touches nothing drops a hand-over. A store that fails the fencing renewal fails the
    /// hand-over instead, and the drain hands the command over again after the grace.
    /// </remarks>
    public static async Task<IntentLease?> StartAsync(IServiceProvider services, Guid intentId, DateTimeOffset? claimedAt)
    {
        var intents = services.GetRequiredService<ICommandIntentStore>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILogger<IntentLease>>();
        var grace = services.GetRequiredService<IOptions<OrleansDispatchOptions>>().Value.IntentGrace;

        var now = timeProvider.GetUtcNow();
        if (claimedAt is { } claim && !await intents.TryRenewFromAsync(intentId, claim, now, CancellationToken.None))
        {
            logger.LogIntentHandOverDropped(intentId);
            return null;
        }

        if (claimedAt is null && RenewsAtStart(intentId, now, grace) && !await TryRenewLateAsync(intents, logger, intentId, now))
        {
            logger.LogIntentHandOverDropped(intentId);
            return null;
        }

        var lease = new IntentLease(intents, timeProvider, logger, intentId);

        var period = grace / 3;
        lease._timer = timeProvider.CreateTimer(static state => ((IntentLease)state!).OnTick(), lease, period, period);
        return lease;
    }

    /// <summary>
    /// Renews an unstamped hand-over that arrives late and reports whether its command is still recorded and not kept: one
    /// that completed through another run while this hand-over was on its way is not run again. A renewal that fails is
    /// logged and the hand-over runs, as it always did.
    /// </summary>
    private static async Task<bool> TryRenewLateAsync(ICommandIntentStore intents, ILogger logger, Guid intentId, DateTimeOffset now)
    {
        try
        {
            return await intents.TryRenewAsync(intentId, now, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogIntentRenewalFailed(ex, intentId);
            return true;
        }
    }

    /// <summary>
    /// Whether the hand-over has to be renewed when execution starts. The record is stamped after its id is
    /// created, and renewals follow every third of the grace; a time-ordered id no older than a sixth of the grace
    /// therefore keeps the record out of the drain until the second renewal, with a sixth of the grace to spare, so
    /// one failed renewal is survived. Any other id — an older one, which is also every resumed command, or one that
    /// carries no time — is renewed.
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
        return now - DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds) >= grace / 6;
    }

    /// <summary>
    /// Logs the failure of this attempt and records it with the command — a concurrency conflict as a conflict, which
    /// gives the attempt back and counts against the conflict bound instead; a failure to record it does not hide the
    /// original one.
    /// </summary>
    public async Task RecordFailureAsync(Exception failure, string commandType, Guid? aggregateId)
    {
        _logger.LogIntentAttemptFailed(failure, _intentId, commandType, aggregateId);
        try
        {
            var description = IntentFailure.Describe(failure);
            await (IntentFailure.IsConflict(failure)
                ? _intents.RecordConflictAsync(_intentId, description, CancellationToken.None)
                : _intents.RecordFailureAsync(_intentId, description, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    /// <summary>
    /// Gives back the attempt this hand-over counted, because the handler stopped with its silo rather than failing;
    /// a failure to give it back leaves the attempt counted.
    /// </summary>
    public async Task ReturnAttemptAsync()
    {
        try
        {
            await _intents.ReturnAttemptAsync(_intentId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    /// <summary>Stops the renewals: no tick starts after this returns, and the one in flight has ended.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_timer is { } timer)
        {
            await timer.DisposeAsync();
        }

        await _inFlight;
        _stop.Dispose();
    }

    /// <summary>A tick on the timer's thread: one renewal at a time; a tick that finds one running does nothing.</summary>
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
            await RenewAsync(_timeProvider.GetUtcNow(), _stop.Token);
        }
        catch (OperationCanceledException)
        {
            // The lease stopped; there is nothing left to renew.
        }
        finally
        {
            Volatile.Write(ref _renewing, 0);
        }
    }

    private async Task RenewAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await _intents.RenewAsync(_intentId, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogIntentRenewalFailed(ex, _intentId);
        }
    }
}
