using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.Timers;

/// <summary>
/// The host-facing side of the timers: one call per owner grain, carrying the caller's token. An owner id or a purpose
/// the reminder table cannot hold is refused before any call, on every member.
/// </summary>
internal sealed class DurableTimers(IGrainFactory grainFactory) : IDurableTimers
{
    public Task RegisterAsync(TimerRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ReminderName.EnsureValidPurpose(registration.Purpose);
        return Owner(registration.OwnerId).RegisterAsync(registration.Purpose, registration.DueAt, cancellationToken);
    }

    public Task CancelAsync(string ownerId, string purpose, CancellationToken cancellationToken = default) =>
        Owner(ownerId).CancelAsync(purpose, cancellationToken);

    public Task CancelAllAsync(string ownerId, CancellationToken cancellationToken = default) =>
        Owner(ownerId).CancelAllAsync(cancellationToken);

    public async Task<IReadOnlyList<TimerRegistration>> ListAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        var names = await Owner(ownerId).ListReminderNamesAsync(cancellationToken);
        return [.. names.Select(name => ReminderName.ToRegistration(ownerId, name))];
    }

    private ITimerOwnerGrain Owner(string ownerId)
    {
        ReminderName.EnsureValidOwnerId(ownerId);
        return grainFactory.GetGrain<ITimerOwnerGrain>(ownerId);
    }
}
