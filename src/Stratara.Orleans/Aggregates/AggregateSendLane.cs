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
/// different aggregates from one scope run in parallel.
/// </remarks>
public sealed class AggregateSendLane
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Task> _tails = new();

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

        return RunAsync(previous, prepare, issue, completed);
    }

    private static async Task<Task> RunAsync(Task previous, Task<AggregateCommandEnvelope> prepare, Func<AggregateCommandEnvelope, Task> issue, TaskCompletionSource completed)
    {
        try
        {
            await previous;
        }
        catch (Exception)
        {
            // An earlier call that failed does not hold back the ones after it.
        }

        try
        {
            var envelope = await prepare;
            var call = issue(envelope);
            _ = call.ContinueWith(_ => completed.TrySetResult(), TaskContinuationOptions.ExecuteSynchronously);
            return call;
        }
        catch (Exception)
        {
            completed.TrySetResult();
            throw;
        }
    }
}
