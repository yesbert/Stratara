using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Stratara.Orleans.Timers;

/// <summary>
/// The grain behind <see cref="IDurableTimers"/>. Each timer is a reminder whose name carries the
/// purpose and the due time, so the reminder table is the only state. A reminder that fires checks
/// the owner, waits if it is early, hands the timer to the host's handler once it is due, and
/// unregisters itself afterwards — or at once, if the owner is gone.
/// </summary>
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
    private const char Separator = '@';

    public static string Encode(string purpose, DateTimeOffset dueAt)
    {
        if (purpose.Contains(Separator))
        {
            throw new ArgumentException($"A timer purpose must not contain '{Separator}'.", nameof(purpose));
        }

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
