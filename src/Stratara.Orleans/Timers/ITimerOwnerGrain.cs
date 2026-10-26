using Orleans.Concurrency;
using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.Timers;

/// <summary>
/// One grain per owner, keyed by the owner id, holding the owner's timers as reminders. Internal:
/// the host reaches timers through <see cref="IDurableTimers"/>, never through the grain.
/// </summary>
[Alias("Stratara.Orleans.ITimerOwnerGrain")]
internal interface ITimerOwnerGrain : IGrainWithStringKey
{
    [Alias("RegisterAsync")]
    Task RegisterAsync(string purpose, DateTimeOffset dueAt, CancellationToken cancellationToken);

    [Alias("CancelAsync")]
    Task CancelAsync(string purpose, CancellationToken cancellationToken);

    [Alias("CancelAllAsync")]
    Task CancelAllAsync(CancellationToken cancellationToken);

    [Alias("ListReminderNamesAsync")]
    Task<List<string>> ListReminderNamesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs the handler of the timer whose reminder <paramref name="reminderName"/> fired. Only ever called by the
    /// activation on itself, one-way: the call carries no response and therefore no response timeout, so the reminder
    /// call that asked for it is answered at once, and it keeps the activation busy for as long as the handler runs.
    /// </summary>
    [OneWay]
    [Alias("FireDueAsync")]
    Task FireDueAsync(string reminderName);
}
