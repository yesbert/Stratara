using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.GrainDirectory;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.Diagnostics;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.Timers;

/// <summary>
/// The grain behind <see cref="IDurableTimers"/>. Each timer is a reminder whose name carries the
/// purpose and the due time, so the reminder table is the only state. A reminder that fires checks
/// the owner, waits if it is early, hands the timer to the host's handler once it is due, and
/// unregisters itself afterwards — or at once, if the owner is gone. The grain is reentrant: its only state
/// is the reminder table, and registering or cancelling by name is idempotent, so a handler that cancels or
/// reschedules its owner's timers, or a fact that reaches the owner while a tick runs, does not wait on the
/// tick's own turn. A tick whose handler outlasts the deactivation budget of a stopping silo has its token cancelled;
/// the reminder stays, so the timer fires again on the next silo.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
[TimersRolePlacementFilter]
[Reentrant]
internal sealed class TimerOwnerGrain(
    IServiceScopeFactory scopeFactory,
    IOptions<DurableTimerOptions> options,
    TimeProvider timeProvider,
    SiloStopSignal stopSignal,
    ILogger<TimerOwnerGrain> logger) : Grain, ITimerOwnerGrain, IRemindable
{
    private readonly TimeSpan _retryPeriod = options.Value.RetryPeriod;
    private readonly TimeSpan _dueTolerance = options.Value.DueTolerance;

    // Reentrancy lets a registration interleave with another at every await; changes to the reminder table run
    // one at a time, so two reschedules of one purpose leave one timer. A tick holds the gate only to unregister
    // itself, never while the handler runs, so a handler that reschedules its owner does not wait on its own tick.
    private readonly SemaphoreSlim _changes = new(1, 1);

    // A tick that arrives while the same timer's handler runs — a handler that outlasts the retry period — must not
    // start the handler again; the timer is only unregistered once its handler has completed.
    private readonly HashSet<string> _firing = new(StringComparer.Ordinal);

    // A registration with the purpose and due time of a tick in flight carries that tick's reminder name; the tick must
    // not unregister it once its handler returns, because it is no longer the reminder that fired.
    private readonly HashSet<string> _renewed = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopSignal.Stopping);
    private readonly HashSet<Task> _ticks = [];

    public async Task RegisterAsync(string purpose, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            await CancelPurposeAsync(purpose, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var dueIn = dueAt - timeProvider.GetUtcNow();
            if (dueIn < TimeSpan.Zero)
            {
                dueIn = TimeSpan.Zero;
            }

            var reminderName = ReminderName.Encode(purpose, dueAt);
            await this.RegisterOrUpdateReminder(reminderName, dueIn, _retryPeriod);
            if (_firing.Contains(reminderName))
            {
                _renewed.Add(reminderName);
            }
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task CancelAsync(string purpose, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            await CancelPurposeAsync(purpose, cancellationToken);
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            foreach (var reminder in await this.GetReminders())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await this.UnregisterReminder(reminder);
            }
        }
        finally
        {
            _changes.Release();
        }
    }

    private async Task CancelPurposeAsync(string purpose, CancellationToken cancellationToken)
    {
        foreach (var reminder in await this.GetReminders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReminderName.Decode(reminder.ReminderName).Purpose == purpose)
            {
                await this.UnregisterReminder(reminder);
            }
        }
    }

    public async Task<List<string>> ListReminderNamesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return [.. (await this.GetReminders()).Select(reminder => reminder.ReminderName)];
    }

    async Task IRemindable.ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!_firing.Add(reminderName))
        {
            return;
        }

        var tick = FireAsync(reminderName);
        _ticks.Add(tick);
        try
        {
            await tick;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                var (purpose, _) = ReminderName.Decode(reminderName);
                logger.LogHandlerStoppedWithSilo("timer", $"owner {this.GetPrimaryKeyString()}, purpose {purpose}");
            }

            throw;
        }
        finally
        {
            _ticks.Remove(tick);
            _firing.Remove(reminderName);
            _renewed.Remove(reminderName);
        }
    }

    /// <summary>Waits for the ticks in flight within the deactivation budget; past it, cancels their handlers' token and waits for them to end.</summary>
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await Aggregates.AggregateGrain.StopRunningAsync(_stopping, _ticks.Count == 0 ? null : Task.WhenAll(_ticks), cancellationToken);
        _stopping.Dispose();
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    private async Task FireAsync(string reminderName)
    {
        var (purpose, dueAt) = ReminderName.Decode(reminderName);
        var ownerId = this.GetPrimaryKeyString();

        using var scope = scopeFactory.CreateScope();
        var owners = TimerPorts.OwnersFor(scope.ServiceProvider, ownerId);
        if (!await owners.ExistsAsync(ownerId, _stopping.Token))
        {
            await UnregisterByNameAsync(reminderName);
            return;
        }

        if (!TimerDueTime.IsDue(timeProvider, dueAt, _dueTolerance, out var firedAt) || await this.GetReminder(reminderName) is null)
        {
            return;
        }

        var handler = TimerPorts.HandlerFor(scope.ServiceProvider, ownerId);
        var gateToken = _stopping.Token;
        try
        {
            await handler.OnDueAsync(new TimerDue(ownerId, purpose, dueAt, firedAt), _stopping.Token);
        }
        catch (CommittedEventsNotPublishedException committed)
        {
            // The handler's events are recorded. Firing the timer again would record them a second time, so the
            // unregister below goes ahead even if the silo is stopping — which may be what ended the handover.
            logger.LogTimerCommittedNotPublished(committed, ownerId, purpose);
            gateToken = CancellationToken.None;
        }

        // Under the gate: a registration for the same purpose and due time that lands between the handler's return
        // and the unregister is a renewal, and its reminder must not be deleted by the tick it renewed. A silo that
        // stops while the gate is held leaves the reminder registered, which fires it again — the safe direction.
        await _changes.WaitAsync(gateToken);
        try
        {
            if (!_renewed.Contains(reminderName))
            {
                await UnregisterByNameAsync(reminderName);
            }
        }
        finally
        {
            _changes.Release();
        }
    }

    private async Task UnregisterByNameAsync(string reminderName)
    {
        var reminder = await this.GetReminder(reminderName);
        if (reminder is not null)
        {
            await this.UnregisterReminder(reminder);
        }
    }
}

/// <summary>Encodes a timer's purpose and due time into a reminder name and back.</summary>
internal static class ReminderName
{
    /// <summary>
    /// The longest purpose a reminder name holds: the reminder table's name column is 150 characters,
    /// and the separator and the due time's ticks take up to twenty of them.
    /// </summary>
    public const int MaxPurposeLength = 130;

    /// <summary>
    /// The longest owner id a timer holds: the owner id is the timer grain's key, and the reminder table's grain-id
    /// column is 150 characters, of which the grain type's name and its separator take the rest.
    /// </summary>
    public const int MaxOwnerIdLength = ReminderStoreGrainIdLength - TimerGrainIdPrefixLength;

    /// <summary>The width of the reminder table's grain-id column.</summary>
    public const int ReminderStoreGrainIdLength = 150;

    /// <summary>What the runtime's string form of the timer grain's id puts before the key: <c>timerowner/</c>.</summary>
    public const int TimerGrainIdPrefixLength = 11;

    private const char Separator = '@';

    /// <summary>Refuses a purpose the reminder table cannot hold or the name cannot be decoded from.</summary>
    /// <exception cref="ArgumentException">The purpose is empty, contains <c>@</c>, or is longer than <see cref="MaxPurposeLength"/>.</exception>
    public static void EnsureValidPurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        if (purpose.Contains(Separator))
        {
            throw new ArgumentException($"A timer purpose must not contain '{Separator}'.", nameof(purpose));
        }

        if (purpose.Length > MaxPurposeLength)
        {
            throw new ArgumentException($"A timer purpose holds at most {MaxPurposeLength} characters; '{purpose[..20]}…' has {purpose.Length}.", nameof(purpose));
        }
    }

    /// <summary>Refuses an owner id the reminder table cannot hold as a grain key.</summary>
    /// <exception cref="ArgumentException">The owner id is empty or longer than <see cref="MaxOwnerIdLength"/>.</exception>
    public static void EnsureValidOwnerId(string ownerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        if (ownerId.Length > MaxOwnerIdLength)
        {
            throw new ArgumentException($"A timer owner id holds at most {MaxOwnerIdLength} characters; '{ownerId[..20]}…' has {ownerId.Length}.", nameof(ownerId));
        }
    }

    public static string Encode(string purpose, DateTimeOffset dueAt)
    {
        EnsureValidPurpose(purpose);
        return string.Concat(purpose, Separator, dueAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    public static (string Purpose, DateTimeOffset DueAt) Decode(string reminderName)
    {
        var separator = reminderName.LastIndexOf(Separator);
        if (separator < 0)
        {
            throw new FormatException($"Reminder '{reminderName}' is not a timer: it carries no due time.");
        }

        var purpose = reminderName[..separator];
        var ticks = long.Parse(reminderName[(separator + 1)..], CultureInfo.InvariantCulture);
        return (purpose, new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    public static TimerRegistration ToRegistration(string ownerId, string reminderName)
    {
        var (purpose, dueAt) = Decode(reminderName);
        return new TimerRegistration(ownerId, purpose, dueAt);
    }
}
