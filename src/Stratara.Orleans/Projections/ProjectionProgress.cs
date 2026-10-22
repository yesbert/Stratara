using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Takes a mark from the reader's committed head of every partition, and waits by asking each partition's projection
/// grain to answer once its checkpoint has reached the mark. A partition whose mark is <c>0</c> holds nothing the mark
/// covers and counts as reached without a call. Each call carries a budget of the time left, capped at
/// <see cref="MaximumBudget"/> so it never meets the runtime's response timeout; a partition that answers short of its
/// mark — the budget passed, or its grain deactivated — is asked again until the time is up. A mark taken under another
/// reader name, which carries the partition count, is not compared with positions it does not belong to.
/// </summary>
internal sealed class ProjectionProgress(
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CommitOrderOptions> commitOrder,
    TimeProvider time) : IProjectionProgress
{
    /// <summary>The longest a single call to a grain waits.</summary>
    internal static readonly TimeSpan MaximumBudget = TimeSpan.FromSeconds(10);

    public async Task<StoreMark> MarkAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICommittedPositionReader>();
        var positions = new long[commitOrder.Value.PartitionCount];
        for (var partition = 0; partition < positions.Length; partition++)
        {
            positions[partition] = await reader.CommittedHeadAsync(partition, cancellationToken);
        }

        return new StoreMark(reader.Name, positions);
    }

    /// <exception cref="InvalidOperationException">No projection of that name is registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
    public Task<ProjectionWaitResult> WaitAsync(string projectionName, StoreMark mark, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        return WaitAsync(
            (handler, projections) => projections.Any(p => handler.GetProjectionName(p) == projectionName)
                ? projectionName
                : throw new InvalidOperationException($"No projection named '{projectionName}' is registered."),
            mark,
            timeout,
            cancellationToken);
    }

    /// <exception cref="InvalidOperationException">No projection of that type is registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
    public Task<ProjectionWaitResult> WaitAsync(Type projectionType, StoreMark mark, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectionType);
        return WaitAsync(
            (handler, projections) => projections.FirstOrDefault(p => p.GetType() == projectionType) is { } projection
                ? handler.GetProjectionName(projection)
                : throw new InvalidOperationException($"No projection of type {projectionType.FullName} is registered."),
            mark,
            timeout,
            cancellationToken);
    }

    private static ProjectionWaitOutcome OutcomeOf(StoreReaderState state) => state switch
    {
        StoreReaderState.StalledOnEntry or StoreReaderState.StalledOnRead => ProjectionWaitOutcome.Stalled,
        StoreReaderState.Suspended => ProjectionWaitOutcome.Suspended,
        _ => ProjectionWaitOutcome.TimedOut,
    };

    private static ProjectionWaitResult Behind(IReadOnlyList<ProjectionPartitionLag> lags)
    {
        var outcome = lags.Any(lag => lag.State == ProjectionWaitOutcome.Stalled) ? ProjectionWaitOutcome.Stalled
            : lags.Any(lag => lag.State == ProjectionWaitOutcome.Suspended) ? ProjectionWaitOutcome.Suspended
            : ProjectionWaitOutcome.TimedOut;
        return new ProjectionWaitResult(outcome, lags);
    }

    private async Task<ProjectionWaitResult> WaitAsync(
        Func<IProjectionHandler, IEnumerable<IProjection>, string> name,
        StoreMark mark,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        string projectionName;
        string readerName;
        using (var scope = scopeFactory.CreateScope())
        {
            var services = scope.ServiceProvider;
            projectionName = name(services.GetRequiredService<IProjectionHandler>(), services.GetServices<IProjection>());
            readerName = services.GetRequiredService<ICommittedPositionReader>().Name;
        }

        var partitionCount = commitOrder.Value.PartitionCount;
        if (mark.Reader != readerName || mark.Positions.Count != partitionCount)
        {
            return new ProjectionWaitResult(ProjectionWaitOutcome.MarkNotApplicable, []);
        }

        var started = time.GetTimestamp();
        var pending = Enumerable.Range(0, partitionCount).Where(partition => mark.Positions[partition] > 0).ToList();
        while (pending.Count > 0)
        {
            var remaining = timeout - time.GetElapsedTime(started);
            var budget = remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining < MaximumBudget ? remaining : MaximumBudget;
            var answers = await Task.WhenAll(pending.Select(partition => grainFactory
                    .GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(projectionName, partition))
                    .WaitForAsync(mark.Positions[partition], budget)))
                .WaitAsync(cancellationToken);

            if (answers.Any(answer => answer.State == StoreReaderState.Retired))
            {
                return new ProjectionWaitResult(ProjectionWaitOutcome.MarkNotApplicable, []);
            }

            var behind = pending.Select((partition, i) => (Partition: partition, Answer: answers[i])).Where(p => !p.Answer.Reached).ToList();
            if (behind.Count == 0)
            {
                break;
            }

            if (time.GetElapsedTime(started) >= timeout)
            {
                return Behind([.. behind.Select(p => new ProjectionPartitionLag(p.Partition, OutcomeOf(p.Answer.State)))]);
            }

            pending = [.. behind.Select(p => p.Partition)];
        }

        return new ProjectionWaitResult(ProjectionWaitOutcome.Reached, []);
    }
}
