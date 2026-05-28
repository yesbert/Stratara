using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Merging.ChangeTracking;

/// <summary>
/// One property's contribution to a change-set produced by
/// <see cref="Stratara.Abstractions.EventSourcing.IChangeSetHandler"/>.
/// Encodes the original snapshot value, the current aggregate value, and the new
/// requested value — sufficient context for 3-way merge or conflict reporting.
/// </summary>
/// <param name="PropertyName">Name of the changed property on the aggregate.</param>
/// <param name="SourceValue">The value the caller started from (i.e. what was on screen).</param>
/// <param name="CurrentValue">The current value on the aggregate (potentially mutated by a concurrent write).</param>
/// <param name="ChangeValue">The new value the caller wants to set.</param>
[ExcludeFromCodeCoverage]
public sealed record ChangeDetail(
    string PropertyName,
    object? SourceValue,
    object? CurrentValue,
    object? ChangeValue
);
