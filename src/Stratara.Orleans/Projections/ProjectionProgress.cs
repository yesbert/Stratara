using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Takes a mark from the reader's committed head of every partition — or, for a mark of streams, of the partitions they
/// fall into only — and waits by asking each partition's projection
/// grain, in rounds, to answer once its checkpoint has reached the mark. A partition whose mark is <c>0</c> holds nothing
/// the mark covers and counts as reached without a call. Every round asks every other partition, those that answered
/// reached before included, and the wait answers reached only when all of them did in the same round: a rebuild or a
/// replay pauses every reader before it empties anything, and a paused reader never answers reached, so a rebuild that
/// began during the wait cannot leave a partition counted on an answer from before it. A partition that is through
/// answers at once. Each call carries a budget of the time left, capped at <see cref="MaximumBudget"/> — well below the
/// runtime's response timeout, and short because a waiting call holds its grain's deactivation for as long. A mark
/// taken under another reader name, which carries the partition count, is not compared with positions it does not
/// belong to. The projections' names come from the silo's <see cref="ProjectionRegistrations"/>.
/// </summary>
internal sealed class ProjectionProgress(
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CommitOrderOptions> commitOrder,
    ProjectionRegistrations registrations,
    TimeProvider time) : IProjectionProgress
{
    /// <summary>The longest a single call to a grain waits.</summary>
    internal static readonly TimeSpan MaximumBudget = TimeSpan.FromSeconds(2);

    public Task<StoreMark> MarkAsync(CancellationToken cancellationToken = default) =>
        MarkOfAsync(Enumerable.Range(0, commitOrder.Value.PartitionCount), cancellationToken);

    /// <summary>The committed head of the partitions the streams fall into, and <c>0</c> — reached at once — in every other.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="streamIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="streamIds"/> names no stream.</exception>
    public Task<StoreMark> MarkAsync(IEnumerable<Guid> streamIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamIds);
        var partitionCount = commitOrder.Value.PartitionCount;
        var touched = streamIds.Select(id => PartitionMap.PartitionOf(BucketCalculator.GetBucketId(id), partitionCount)).Distinct().ToList();
        if (touched.Count == 0)
        {
            throw new ArgumentException("A mark of streams needs at least one stream; take MarkAsync() for everything committed.", nameof(streamIds));
        }

        return MarkOfAsync(touched, cancellationToken);
    }

    /// <summary>The committed head of <paramref name="partitions"/>, and <c>0</c> in every other.</summary>
    private async Task<StoreMark> MarkOfAsync(IEnumerable<int> partitions, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICommittedPositionReader>();
        var positions = new long[commitOrder.Value.PartitionCount];
        foreach (var partition in partitions)
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
            names => names.Contains(projectionName)
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
            names => names.NameOf(projectionType) is { } name
                ? name
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
        if (lags.Any(lag => lag.State == ProjectionWaitOutcome.Stalled))
        {
            return new ProjectionWaitResult(ProjectionWaitOutcome.Stalled, lags);
        }

        return lags.Any(lag => lag.State == ProjectionWaitOutcome.Suspended)
            ? new ProjectionWaitResult(ProjectionWaitOutcome.Suspended, lags)
            : new ProjectionWaitResult(ProjectionWaitOutcome.TimedOut, lags);
    }

    private async Task<ProjectionWaitResult> WaitAsync(
        Func<ProjectionRegistrations, string> name,
        StoreMark mark,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        string readerName;
        using (var scope = scopeFactory.CreateScope())
        {
            var services = scope.ServiceProvider;
            readerName = services.GetRequiredService<ICommittedPositionReader>().Name;
        }

        var projectionName = name(registrations);

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
            var budget = remaining < MaximumBudget ? remaining : MaximumBudget;
            if (budget < TimeSpan.Zero)
            {
                budget = TimeSpan.Zero;
            }

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
        }

        return new ProjectionWaitResult(ProjectionWaitOutcome.Reached, []);
    }
}
