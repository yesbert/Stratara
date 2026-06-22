using Stratara.Abstractions.EventSourcing;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Default <see cref="ISnapshotStrategy"/>: writes a snapshot once a stream has advanced by at
/// least a fixed threshold of versions since its most recent snapshot. Applies the same
/// threshold (<see cref="DefaultThreshold"/> unless overridden) to every aggregate type,
/// reproducing the framework's historical "snapshot every 50 events" behaviour.
/// </summary>
/// <remarks>
/// Registered by <c>AddEventSourcing()</c> via <c>TryAddSingleton</c>, so a consumer-supplied
/// <see cref="ISnapshotStrategy"/> overrides it. Construct with a custom threshold to keep the
/// uniform policy but change the cadence.
/// </remarks>
public sealed class VersionThresholdSnapshotStrategy : ISnapshotStrategy
{
    /// <summary>The threshold used when none is supplied: snapshot every 50 versions.</summary>
    public const int DefaultThreshold = 50;

    private readonly int _threshold;

    /// <summary>
    /// Initialises the strategy with the version distance that triggers a new snapshot.
    /// </summary>
    /// <param name="threshold">
    /// Number of versions a stream must advance past its last snapshot before a new one is written.
    /// Defaults to <see cref="DefaultThreshold"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="threshold"/> is less than 1.</exception>
    public VersionThresholdSnapshotStrategy(int threshold = DefaultThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        _threshold = threshold;
    }

    /// <inheritdoc/>
    public bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion) =>
        currentVersion - lastSnapshotVersion >= _threshold;
}
