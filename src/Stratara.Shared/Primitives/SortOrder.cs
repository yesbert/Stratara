namespace Stratara.Shared.Primitives;

/// <summary>
/// Tri-state sort-direction marker used by query / paging APIs that take an optional ordering hint.
/// </summary>
public enum SortOrder
{
    /// <summary>No ordering is applied; the caller accepts whatever order the source returns.</summary>
    Unsorted,

    /// <summary>Ascending order (smallest / earliest first).</summary>
    Ascending,

    /// <summary>Descending order (largest / latest first).</summary>
    Descending
}
