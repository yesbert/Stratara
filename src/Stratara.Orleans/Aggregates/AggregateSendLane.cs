namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Keeps the calls to one aggregate's grain in the order they were initiated within a scope, by
/// issuing each call only after the previous call to the same aggregate has completed. The runtime
/// makes no promise about the delivery order of calls that are in flight at the same time, and a
/// dispatch serialises and records asynchronously before it sends — so the only order a caller can
/// rely on is the one this lane enforces: one call at a time per aggregate, established
/// synchronously at initiation, before the first await.
/// </summary>
/// <remarks>
/// Scoped: the order is a promise between dispatches from one scope — one request, one handler,
/// one unit of work. Across scopes the framework promises nothing, as it never has. Commands to
/// different aggregates from one scope run in parallel. A key whose last call has completed is
/// forgotten, so a scope that dispatches to many aggregates holds only the ones still in flight.
/// </remarks>
internal sealed class AggregateSendLane
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Task> _tails = new();

    /// <summary>
    /// The key a hand-over is ordered under: its aggregate, or its intent when it names none or is heavy work. Heavy
    /// work runs outside the aggregate's turn for the length of its unit, so ordering it under the aggregate would
    /// hold every later call to the aggregate for that long.
    /// </summary>
    public static Guid KeyOf(Guid intentId, Guid? aggregateId, bool heavy) =>
        heavy ? intentId : aggregateId ?? intentId;

    /// <summary>How many keys still have a call in flight.</summary>
    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _tails.Count;
            }
        }
    }

    /// <summary>
    /// Issues the call after every earlier call to <paramref name="key"/> from this scope has
    /// completed. The outer task completes once the call has been issued; the inner task is the call.
    /// </summary>
    /// <param name="key">The aggregate, or the intent for a command that names no aggregate.</param>
    /// <param name="prepare">What must be ready before the call — the envelope, the durable record.</param>
    /// <param name="issue">Issues the grain call.</param>
    /// <returns>Completes when the call was issued, with the call to await or ignore.</returns>
    public Task<Task> SendAsync(Guid key, Task<AggregateCommandEnvelope> prepare, Func<AggregateCommandEnvelope, Task> issue)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            previous = _tails.TryGetValue(key, out var tail) ? tail : Task.CompletedTask;
            _tails[key] = completed.Task;
        }

        return RunAsync(key, previous, prepare, issue, completed);
    }

    /// <summary>
    /// Waits for the previous call to complete — however it completed, because an earlier call that
    /// failed does not hold back the ones after it — then prepares and issues this one. The lane's
    /// tail is released when the call completes, or at once if it could not be issued.
    /// </summary>
    private async Task<Task> RunAsync(Guid key, Task previous, Task<AggregateCommandEnvelope> prepare, Func<AggregateCommandEnvelope, Task> issue, TaskCompletionSource completed)
    {
        await Task.WhenAny(previous);

        Task? call = null;
        try
        {
            var envelope = await prepare;
            call = issue(envelope);
        }
        finally
        {
            if (call is null)
            {
                Release(key, completed);
            }
        }

        _ = call.ContinueWith(_ => Release(key, completed), TaskContinuationOptions.ExecuteSynchronously);
        return call;
    }

    /// <summary>Completes this call's tail and forgets the key when no later call has queued behind it.</summary>
    private void Release(Guid key, TaskCompletionSource completed)
    {
        completed.TrySetResult();
        lock (_gate)
        {
            if (_tails.TryGetValue(key, out var tail) && ReferenceEquals(tail, completed.Task))
            {
                _tails.Remove(key);
            }
        }
    }
}
