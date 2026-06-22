using Stratara.Abstractions.EventSourcing;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// <see cref="ISnapshotStrategy"/> that never writes a snapshot — the supported way to disable
/// snapshotting globally. Streams are always rebuilt by replaying their full event history.
/// </summary>
/// <remarks>
/// Register with <c>services.AddSingleton&lt;ISnapshotStrategy, NoSnapshotStrategy&gt;()</c> to
/// override the default <see cref="VersionThresholdSnapshotStrategy"/>. Useful for short-lived
/// aggregates, tests, or deployments where snapshot storage is undesirable.
/// </remarks>
public sealed class NoSnapshotStrategy : ISnapshotStrategy
{
    /// <inheritdoc/>
    public bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion) => false;
}
