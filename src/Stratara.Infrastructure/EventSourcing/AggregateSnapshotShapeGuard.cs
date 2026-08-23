using System.Reflection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.Reflections;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Hosted service that fails fast at start-up when a registered aggregate declares a property that
/// cannot be set from outside the type.
/// </summary>
/// <remarks>
/// <para>
/// Such a property is not restored from a snapshot. The aggregate rebuilds without complaint, the
/// events recorded after the snapshot apply, and the state the snapshot held for that property is
/// simply gone — so the damage grows the better snapshotting works, and nothing reports it. The
/// constraint had no mechanical enforcement and no diagnostic; this is the diagnostic.
/// </para>
/// <para>
/// The scan walks the trusted-type resolver's registered types once at start-up, which is where the
/// aggregates registered through <c>AddAggregatesFromAssemblyContaining</c> land.
/// </para>
/// </remarks>
internal sealed class AggregateSnapshotShapeGuard(ITrustedTypeResolver typeResolver) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        List<string> violations = [];

        foreach (var aggregateType in typeResolver.RegisteredTypes.Where(IsAggregate))
        {
            var unsettable = UnsettableProperties(aggregateType);
            if (unsettable.Count > 0)
            {
                violations.Add($"{aggregateType.Name}: {string.Join(", ", unsettable)}");
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "These aggregate properties cannot be set from outside their type, so a snapshot restore " +
                "silently drops the state they held — the aggregate rebuilds, later events apply, and what " +
                "the snapshot captured for them is gone. Give each a public setter (aggregates use " +
                "'public set', not 'private set', because snapshot deserialization needs the setter): " +
                Environment.NewLine + string.Join(Environment.NewLine, violations.Order(StringComparer.Ordinal)));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsAggregate(Type type) =>
        typeof(IAggregate).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false };

    private static List<string> UnsettableProperties(Type aggregateType) =>
        [.. aggregateType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property => property.SetMethod is null || !property.SetMethod.IsPublic)
            .Where(property => HoldsState(aggregateType, property))
            .Select(property => property.Name)];

    /// <summary>
    /// Whether the property stores something a snapshot would have to restore. A computed property
    /// (<c>public bool IsOverdrawn =&gt; Balance &lt; 0;</c>) has no backing field: it is recomputed
    /// after a restore and loses nothing, so requiring a setter for it would force a consumer to
    /// break working code. An auto-property without a public setter does hold state, and that state
    /// is what disappears.
    /// </summary>
    private static bool HoldsState(Type aggregateType, PropertyInfo property) =>
        aggregateType.GetField(
            $"<{property.Name}>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance) is not null;
}
