using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Takes a mark from the reader's committed head of every partition, and waits by asking each partition's projection
/// grain, in rounds, to answer once its checkpoint has reached the mark. A partition whose mark is <c>0</c> holds nothing
/// the mark covers and counts as reached without a call. Every round asks every other partition, those that answered
/// reached before included, and the wait answers reached only when all of them did in the same round: a rebuild or a
/// replay pauses every reader before it empties anything, and a paused reader never answers reached, so a rebuild that
/// began during the wait cannot leave a partition counted on an answer from before it. A partition that is through
/// answers at once. Each call carries a budget of the time left, capped at <see cref="MaximumBudget"/> — well below the
/// runtime's response timeout, and short because a waiting call holds its grain's deactivation for as long. A mark
/// taken under another reader name, which carries the partition count, is not compared with positions it does not
/// belong to. The projections' names are resolved once: the registered projections do not change while the host runs.
/// </summary>
internal sealed class ProjectionProgress(
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CommitOrderOptions> commitOrder,
    TimeProvider time) : IProjectionProgress
{
    /// <summary>The longest a single call to a grain waits.</summary>
    internal static readonly TimeSpan MaximumBudget = TimeSpan.FromSeconds(2);

    private ProjectionNames? _names;

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
            names => names.ByName.Contains(projectionName)
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
            names => names.ByType.TryGetValue(projectionType, out var name)
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
        Func<ProjectionNames, string> name,
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
            _names ??= ProjectionNames.Of(services.GetRequiredService<IProjectionHandler>(), services.GetServices<IProjection>());
            readerName = services.GetRequiredService<ICommittedPositionReader>().Name;
        }

        var projectionName = name(_names);

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

    /// <summary>The registered projections' names, by name and by type.</summary>
    private sealed record ProjectionNames(IReadOnlySet<string> ByName, IReadOnlyDictionary<Type, string> ByType)
    {
        public static ProjectionNames Of(IProjectionHandler handler, IEnumerable<IProjection> projections)
        {
            var byType = new Dictionary<Type, string>();
            foreach (var projection in projections)
            {
                byType.TryAdd(projection.GetType(), handler.GetProjectionName(projection));
            }

            return new ProjectionNames(byType.Values.ToHashSet(StringComparer.Ordinal), byType);
        }
    }
}
