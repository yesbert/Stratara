using System.Linq.Expressions;
using Stratara.Abstractions.Merging.ChangeTracking;
using System.Reflection;

namespace Stratara.Shared.Merging.ChangeTracking;

/// <summary>
/// Diff-only counterpart to <see cref="ChangeMerger{TBase, TChanges}"/>. Produces the list of
/// per-property differences between the caller-visible source, the live current state, and the
/// incoming change-set payload without applying any mutations.
/// </summary>
/// <typeparam name="TBase">CLR type of the aggregate / read model the changes apply to.</typeparam>
/// <typeparam name="TChanges">CLR type of the change-set payload (e.g. an update command).</typeparam>
/// <remarks>
/// Property accessors are compiled and cached per-type-combination. Properties that exist on
/// <typeparamref name="TChanges"/> but not on <typeparamref name="TBase"/> are skipped — only
/// comparable property pairs surface in the result.
/// </remarks>
public static class ChangeSetBuilder<TBase, TChanges>
    where TBase : notnull
    where TChanges : notnull
{
    private static readonly PropertyAccessor[] s_accessors = typeof(TChanges) // NOSONAR — intentional: per-type-combination caching is the goal; each generic type combination has its own s_accessors instance
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p is { CanRead: true } && p.GetIndexParameters().Length == 0)
        .Select(p => new PropertyAccessor(p))
        .ToArray();

    /// <summary>
    /// Returns the per-property <see cref="ChangeDetail"/> entries for fields whose incoming value
    /// differs from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The aggregate / read-model snapshot the caller observed when authoring the change-set.</param>
    /// <param name="current">The live aggregate state to surface alongside each diff.</param>
    /// <param name="changes">The incoming change-set payload.</param>
    /// <returns>Read-only list of <see cref="ChangeDetail"/> entries for changed fields; never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<ChangeDetail> CreateChangeSet(TBase source, TBase current, TChanges changes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);

        var differences = new List<ChangeDetail>();

        foreach (var accessor in s_accessors)
        {
            var changeVal = accessor.ChangeGetter(changes);

            if (!accessor.CanCompare)
            {
                continue;
            }

            var sourceVal = accessor.BaseGetter!(source);
            var currentVal = accessor.BaseGetter!(current);

            if (!Equals(sourceVal, changeVal))
            {
                differences.Add(new ChangeDetail(accessor.PropertyName, sourceVal, currentVal, changeVal));
            }
        }

        return differences;
    }

    private sealed class PropertyAccessor
    {
        public PropertyAccessor(PropertyInfo changeProp)
        {
            PropertyName = changeProp.Name;
            ChangeGetter = CompileGetter<TChanges>(changeProp);

            var baseProp = typeof(TBase).GetProperty(changeProp.Name);
            if (baseProp is { CanRead: true })
            {
                BaseGetter = CompileGetter<TBase>(baseProp);
                CanCompare = true;
                return;
            }

            BaseGetter = null;
            CanCompare = false;
        }

        public string PropertyName { get; }
        public Func<TChanges, object?> ChangeGetter { get; }
        public Func<TBase, object?>? BaseGetter { get; }
        public bool CanCompare { get; }

        private static Func<TObj, object?> CompileGetter<TObj>(PropertyInfo prop)
        {
            var param = Expression.Parameter(typeof(TObj), "x");
            var body = Expression.Convert(Expression.Property(param, prop), typeof(object));
            return Expression.Lambda<Func<TObj, object?>>(body, param).Compile();
        }
    }
}
