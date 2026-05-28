using System.Diagnostics.CodeAnalysis;
using Stratara.Abstractions.Merging.ChangeTracking;

namespace Stratara.Shared.Merging.ChangeTracking;

/// <summary>
/// Result of a <c>ChangeMerger</c> operation: the merged <typeparamref name="TChanges"/> payload
/// plus the per-property change details that constitute the merge's audit trail.
/// </summary>
/// <typeparam name="TChanges">CLR type of the change-set payload (typically a command).</typeparam>
/// <param name="MergedChanges">The merged payload with conflicting fields resolved against the current aggregate state.</param>
/// <param name="Differences">Per-property change details describing source, current, and incoming values.</param>
[ExcludeFromCodeCoverage]
public sealed record ChangeMergeResult<TChanges>(
    TChanges MergedChanges,
    IReadOnlyList<ChangeDetail> Differences
);
