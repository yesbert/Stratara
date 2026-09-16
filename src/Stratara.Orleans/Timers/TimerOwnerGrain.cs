using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.GrainDirectory;
using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.Timers;

/// <summary>
/// The grain behind <see cref="IDurableTimers"/>. Each timer is a reminder whose name carries the
/// purpose and the due time, so the reminder table is the only state. A reminder that fires checks
/// the owner, waits if it is early, hands the timer to the host's handler once it is due, and
/// unregisters itself afterwards — or at once, if the owner is gone. The grain is reentrant: its only state
/// is the reminder table, and registering or cancelling by name is idempotent, so a handler that cancels or
/// reschedules its owner's timers, or a fact that reaches the owner while a tick runs, does not wait on the
/// tick's own turn.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
[Reentrant]
internal sealed class TimerOwnerGrain(
    IServiceScopeFactory scopeFactory,
    IOptions<DurableTimerOptions> options,
    TimeProvider timeProvider) : Grain, ITimerOwnerGrain, IRemindable
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

            await this.RegisterOrUpdateReminder(ReminderName.Encode(purpose, dueAt), dueIn, _retryPeriod);
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

        try
        {
            await FireAsync(reminderName);
        }
        finally
        {
            _firing.Remove(reminderName);
        }
    }

    private async Task FireAsync(string reminderName)
    {
        var (purpose, dueAt) = ReminderName.Decode(reminderName);
        var ownerId = this.GetPrimaryKeyString();

        using var scope = scopeFactory.CreateScope();
        var owners = TimerPorts.OwnersFor(scope.ServiceProvider, ownerId);
        if (!await owners.ExistsAsync(ownerId, CancellationToken.None))
        {
            await UnregisterByNameAsync(reminderName);
            return;
        }

        if (!TimerDueTime.IsDue(timeProvider, dueAt, _dueTolerance, out var firedAt) || await this.GetReminder(reminderName) is null)
        {
            return;
        }

        var handler = TimerPorts.HandlerFor(scope.ServiceProvider, ownerId);
        await handler.OnDueAsync(new TimerDue(ownerId, purpose, dueAt, firedAt), CancellationToken.None);

        await UnregisterByNameAsync(reminderName);
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
