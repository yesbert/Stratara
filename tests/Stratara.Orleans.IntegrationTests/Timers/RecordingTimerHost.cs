using System.Collections.Concurrent;
using Stratara.Orleans.Timers;

namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>
/// The host's side of the timer port, as a test double: owners are a set the test adds to and
/// removes from, and every firing is recorded with the owner it was delivered for.
/// </summary>
public sealed class RecordingTimerHost : ITimerOwners, ITimerHandler
{
    private readonly ConcurrentDictionary<string, byte> _owners = new(StringComparer.Ordinal);

    public ConcurrentQueue<TimerDue> Fired { get; } = new();

    public void AddOwner(string ownerId) => _owners[ownerId] = 0;

    public void RemoveOwner(string ownerId) => _owners.TryRemove(ownerId, out _);

    public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(_owners.ContainsKey(ownerId));

    public Task OnDueAsync(TimerDue due, CancellationToken cancellationToken)
    {
        Fired.Enqueue(due);
        return Task.CompletedTask;
    }

    public int FiringsFor(string ownerId) => Fired.Count(due => due.OwnerId == ownerId);
}
