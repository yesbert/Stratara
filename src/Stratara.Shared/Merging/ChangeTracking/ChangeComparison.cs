using System.Collections;

namespace Stratara.Shared.Merging.ChangeTracking;

/// <summary>
/// The two decisions the change-tracking primitives share: whether a pair of properties may be
/// compared at all, and whether two of their values are the same.
/// </summary>
internal static class ChangeComparison
{
    /// <summary>
    /// Whether a value of <paramref name="from"/> can be stored in a property of <paramref name="to"/>.
    /// Nullability of a value type is ignored on both sides, so <c>int</c> and <c>int?</c> are
    /// compatible in either direction.
    /// </summary>
    public static bool AreCompatible(Type from, Type to)
    {
        var source = Nullable.GetUnderlyingType(from) ?? from;
        var target = Nullable.GetUnderlyingType(to) ?? to;
        return target.IsAssignableFrom(source);
    }

    /// <summary>
    /// Whether two property values are the same. Collections are compared by content rather than by
    /// reference: a value that crossed a process boundary is never the instance it was read from, and
    /// comparing references would report every collection as changed. Dictionaries are compared key by
    /// key, every other sequence element by element in order.
    /// </summary>
    public static bool AreEqual(object? left, object? right)
    {
        if (Equals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left is string || right is string)
        {
            return false;
        }

        if (left is IDictionary leftMap && right is IDictionary rightMap)
        {
            return DictionariesAreEqual(leftMap, rightMap);
        }

        return left is IEnumerable leftItems && right is IEnumerable rightItems
            && SequencesAreEqual(leftItems, rightItems);
    }

    private static bool DictionariesAreEqual(IDictionary left, IDictionary right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (DictionaryEntry entry in left)
        {
            if (!right.Contains(entry.Key) || !AreEqual(entry.Value, right[entry.Key]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequencesAreEqual(IEnumerable left, IEnumerable right)
    {
        var leftItems = left.GetEnumerator();
        var rightItems = right.GetEnumerator();
        try
        {
            while (true)
            {
                var leftHasItem = leftItems.MoveNext();
                var rightHasItem = rightItems.MoveNext();
                if (leftHasItem != rightHasItem)
                {
                    return false;
                }

                if (!leftHasItem)
                {
                    return true;
                }

                if (!AreEqual(leftItems.Current, rightItems.Current))
                {
                    return false;
                }
            }
        }
        finally
        {
            (leftItems as IDisposable)?.Dispose();
            (rightItems as IDisposable)?.Dispose();
        }
    }
}
