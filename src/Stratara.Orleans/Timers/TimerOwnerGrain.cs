using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.GrainDirectory;
using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.Timers;

/// <summary>
/// The grain behind <see cref="IDurableTimers"/>. Each timer is a reminder whose name carries the
/// purpose and the due time, so the reminder table is the only state. A reminder that fires checks
/// the owner, waits if it is early, hands the timer to the host's handler once it is due, and
/// unregisters itself afterwards — or at once, if the owner is gone.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
internal sealed class TimerOwnerGrain(
    IServiceScopeFactory scopeFactory,
    IOptions<DurableTimerOptions> options,
    TimeProvider timeProvider) : Grain, ITimerOwnerGrain, IRemindable
{
    private readonly TimeSpan _retryPeriod = options.Value.RetryPeriod;

    public async Task RegisterAsync(string purpose, DateTimeOffset dueAt)
    {
        await CancelAsync(purpose);

        var dueIn = dueAt - timeProvider.GetUtcNow();
        if (dueIn < TimeSpan.Zero)
        {
            dueIn = TimeSpan.Zero;
        }

        await this.RegisterOrUpdateReminder(ReminderName.Encode(purpose, dueAt), dueIn, _retryPeriod);
    }

    public async Task CancelAsync(string purpose)
    {
        foreach (var reminder in await this.GetReminders())
        {
            if (ReminderName.Decode(reminder.ReminderName).Purpose == purpose)
            {
                await this.UnregisterReminder(reminder);
            }
        }
    }

    public async Task CancelAllAsync()
    {
        foreach (var reminder in await this.GetReminders())
        {
            await this.UnregisterReminder(reminder);
        }
    }

    public async Task<List<string>> ListReminderNamesAsync() =>
        [.. (await this.GetReminders()).Select(reminder => reminder.ReminderName)];

    async Task IRemindable.ReceiveReminder(string reminderName, TickStatus status)
    {
        var (purpose, dueAt) = ReminderName.Decode(reminderName);
        var ownerId = this.GetPrimaryKeyString();

        using var scope = scopeFactory.CreateScope();
        var owners = scope.ServiceProvider.GetRequiredService<ITimerOwners>();
        if (!await owners.ExistsAsync(ownerId, CancellationToken.None))
        {
            await UnregisterByNameAsync(reminderName);
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (now < dueAt)
        {
            return;
        }

        var handler = scope.ServiceProvider.GetRequiredService<ITimerHandler>();
        await handler.OnDueAsync(new TimerDue(ownerId, purpose, dueAt, now), CancellationToken.None);

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
