namespace Stratara.Orleans.Timers;

/// <summary>
/// One grain per owner, keyed by the owner id, holding the owner's timers as reminders. Internal:
/// the host reaches timers through <see cref="IDurableTimers"/>, never through the grain.
/// </summary>
internal interface ITimerOwnerGrain : IGrainWithStringKey
{
    Task RegisterAsync(string purpose, DateTimeOffset dueAt);

    Task CancelAsync(string purpose);

    Task CancelAllAsync();

    Task<List<string>> ListReminderNamesAsync();
}
