using System.Collections;
using System.Collections.Concurrent;

namespace Stratara.Shared.Merging.ChangeTracking;

/// <summary>
/// The decisions the change-tracking primitives share: whether a pair of properties may be compared
/// at all, and whether two of their values are the same.
/// </summary>
internal static class ChangeComparison
{
    private const int MaxDepth = 16;

    private static readonly ConcurrentDictionary<Type, bool> s_isSet = new();

    /// <summary>
    /// Whether a value of <paramref name="from"/> can be assigned to a property of
    /// <paramref name="to"/>. Nullability of a value type is ignored on both sides — whether a
    /// particular absent value fits is decided per value, by <see cref="AcceptsNull"/>.
    /// </summary>
    public static bool IsAssignable(Type from, Type to)
    {
        var source = Nullable.GetUnderlyingType(from) ?? from;
        var target = Nullable.GetUnderlyingType(to) ?? to;
        return target.IsAssignableFrom(source);
    }

    /// <summary>
    /// Whether a value of <paramref name="from"/> can be recorded as a change to a property of
    /// <paramref name="to"/>. A recorded change travels as JSON and is read back into the property's
    /// own type, so beyond assignable types two sequences fit each other where their elements do —
    /// an update that carries a read-only list for an aggregate's list is still an update to it.
    /// </summary>
    public static bool CanBeRecorded(Type from, Type to)
    {
        if (IsAssignable(from, to))
        {
            return true;
        }

        var sourceElement = ElementTypeOf(Nullable.GetUnderlyingType(from) ?? from);
        var targetElement = ElementTypeOf(Nullable.GetUnderlyingType(to) ?? to);
        return sourceElement is not null && targetElement is not null && IsAssignable(sourceElement, targetElement);
    }

    /// <summary>Whether a property of <paramref name="type"/> can hold an absent value.</summary>
    public static bool AcceptsNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    /// <summary>
    /// Whether two property values are the same. Lists, arrays, sets and dictionaries are compared by
    /// content rather than by reference: a value that crossed a process boundary is never the instance
    /// it was read from, and comparing references would report every collection as changed. Lists are
    /// compared element by element in order, sets regardless of order, dictionaries key by key. Every
    /// other type — a lazy sequence included — is compared by its own equality.
    /// </summary>
    public static bool AreEqual(object? left, object? right) => AreEqual(left, right, depth: 0);

    private static bool AreEqual(object? left, object? right, int depth)
    {
        if (Equals(left, right))
        {
            return true;
        }

        // A graph nested deeper than this is reported as changed rather than followed any further.
        if (left is null || right is null || depth >= MaxDepth)
        {
            return false;
        }

        if (left is IDictionary leftMap && right is IDictionary rightMap)
        {
            return DictionariesAreEqual(leftMap, rightMap, depth);
        }

        var leftIsSet = IsSet(left);
        var rightIsSet = IsSet(right);
        if ((leftIsSet || rightIsSet) && (leftIsSet || left is IList) && (rightIsSet || right is IList))
        {
            return SetsAreEqual((IEnumerable)left, (IEnumerable)right);
        }

        return left is IList leftList && right is IList rightList && ListsAreEqual(leftList, rightList, depth);
    }

    private static bool DictionariesAreEqual(IDictionary left, IDictionary right, int depth)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (DictionaryEntry entry in left)
        {
            if (!right.Contains(entry.Key) || !AreEqual(entry.Value, right[entry.Key], depth + 1))
            {
                return false;
            }
        }

        // The two sides may compare keys differently; a key only one of them recognises is a difference.
        foreach (var key in right.Keys)
        {
            if (!left.Contains(key))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ListsAreEqual(IList left, IList right, int depth)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!AreEqual(left[i], right[i], depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SetsAreEqual(IEnumerable left, IEnumerable right)
    {
        var leftItems = left.Cast<object?>().ToList();
        var rightItems = right.Cast<object?>().ToList();
        if (leftItems.Count != rightItems.Count)
        {
            return false;
        }

        var lookup = new HashSet<object?>(rightItems);
        return leftItems.TrueForAll(lookup.Contains);
    }

    private static bool IsSet(object value) =>
        s_isSet.GetOrAdd(value.GetType(), static type => Array.Exists(type.GetInterfaces(), IsSetInterface));

    private static bool IsSetInterface(Type candidate) =>
        candidate.IsGenericType
        && (candidate.GetGenericTypeDefinition() == typeof(ISet<>) || candidate.GetGenericTypeDefinition() == typeof(IReadOnlySet<>));

    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string) || typeof(IDictionary).IsAssignableFrom(type))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetArrayRank() == 1 ? type.GetElementType() : null;
        }

        var sequence = IsSequenceInterface(type) ? type : Array.Find(type.GetInterfaces(), IsSequenceInterface);
        return sequence?.GetGenericArguments()[0];
    }

    private static bool IsSequenceInterface(Type candidate) =>
        candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>);
}
