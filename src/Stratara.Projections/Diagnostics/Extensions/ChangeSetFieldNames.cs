using Stratara.Abstractions.Merging.ChangeTracking;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Deferred-formatting wrapper around an <see cref="IReadOnlyList{ChangeDetail}"/> for the
/// source-generated <c>LogChangeSetCreated</c> logger extension. The wrapping struct is zero-cost
/// at construction; the join over property names happens only inside
/// <see cref="ToString"/>, which the source-generated logger formatter invokes solely when
/// the target log channel is enabled — keeping the hot path allocation-free when Debug logging
/// is disabled.
/// </summary>
/// <remarks>
/// Only the changed property names are emitted; values stay inside the wrapper and are never
/// stringified, satisfying the PII guard from Round-3-Audit Finding R3-Sec-005.
/// </remarks>
public readonly struct ChangeSetFieldNames
{
    private readonly IReadOnlyList<ChangeDetail> _changes;

    /// <summary>Initializes a new wrapper over the supplied change list.</summary>
    /// <param name="changes">The detected field changes; must not be <see langword="null"/>.</param>
    public ChangeSetFieldNames(IReadOnlyList<ChangeDetail> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        _changes = changes;
    }

    /// <summary>Renders the wrapped change list as a comma-separated list of property names.</summary>
    /// <returns>A names-only projection of the change set, never including values.</returns>
    public override string ToString() => string.Join(", ", _changes.Select(c => c.PropertyName));
}
