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
    Task RegisterAsync(string purpose, DateTimeOffset dueAt);

    [Alias("CancelAsync")]
    Task CancelAsync(string purpose);

    [Alias("CancelAllAsync")]
    Task CancelAllAsync();

    [Alias("ListReminderNamesAsync")]
    Task<List<string>> ListReminderNamesAsync();
}
