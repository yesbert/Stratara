using System.Linq.Expressions;
using System.Reflection;
using Stratara.Abstractions.Merging.ChangeTracking;

namespace Stratara.Shared.Merging.ChangeTracking;

/// <summary>
/// Last-writer-wins change merger for an update operation against the current aggregate state.
/// Compares each property of the incoming <typeparamref name="TChanges"/> payload against the
/// <typeparamref name="TBase"/> source the caller saw at command time; when they match, the current
/// aggregate value wins (avoiding lost updates), otherwise the incoming value wins.
/// </summary>
/// <typeparam name="TBase">CLR type of the aggregate / read model the changes apply to.</typeparam>
/// <typeparam name="TChanges">CLR type of the change-set payload (e.g. an update command).</typeparam>
/// <remarks>
/// Property accessors are compiled to delegates and cached per-type-combination for the lifetime of
/// the process. Properties that exist on <typeparamref name="TChanges"/> but not on
/// <typeparamref name="TBase"/> are copied through unchanged.
/// </remarks>
public static class ChangeMerger<TBase, TChanges>
    where TBase : notnull
    where TChanges : new()
{
    private static readonly PropertyAccessor[] s_accessors = typeof(TChanges) // NOSONAR — intentional: per-type-combination caching is the goal; each generic type combination has its own s_accessors instance
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)
        .Select(p => new PropertyAccessor(p))
        .ToArray();

    /// <summary>
    /// Applies <paramref name="changes"/> against the diff of <paramref name="source"/> (caller's
    /// view) and <paramref name="current"/> (live aggregate) using last-writer-wins semantics per
    /// property.
    /// </summary>
    /// <param name="source">The aggregate / read-model snapshot the caller observed when authoring the change-set.</param>
    /// <param name="current">The live aggregate state to merge against.</param>
    /// <param name="changes">The incoming change-set payload (e.g. update command).</param>
    /// <returns>A <see cref="ChangeMergeResult{TChanges}"/> with the merged payload and per-property differences.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is <see langword="null"/>.</exception>
    public static ChangeMergeResult<TChanges> ApplyChanges(TBase source, TBase current, TChanges changes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);

        var result = new TChanges();
        var differences = new List<ChangeDetail>();

        foreach (var accessor in s_accessors)
        {
            var changeVal = accessor.ChangeGetter(changes);

            if (!accessor.CanCompare)
            {
                accessor.ChangeSetter(result, changeVal);
                continue;
            }

            var sourceVal = accessor.BaseGetter!(source);
            var currentVal = accessor.BaseGetter!(current);

            if (Equals(sourceVal, changeVal))
            {
                accessor.ChangeSetter(result, currentVal);
                continue;
            }

            accessor.ChangeSetter(result, changeVal);
            differences.Add(new ChangeDetail(accessor.PropertyName, sourceVal, currentVal, changeVal));
        }

        return new ChangeMergeResult<TChanges>(result, differences);
    }

    private sealed class PropertyAccessor
    {
        public PropertyAccessor(PropertyInfo changeProp)
        {
            PropertyName = changeProp.Name;
            ChangeGetter = CompileGetter<TChanges>(changeProp);
            ChangeSetter = CompileSetter<TChanges>(changeProp);

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
        public Action<TChanges, object?> ChangeSetter { get; }
        public Func<TBase, object?>? BaseGetter { get; }
        public bool CanCompare { get; }

        private static Func<TObj, object?> CompileGetter<TObj>(PropertyInfo prop)
        {
            var param = Expression.Parameter(typeof(TObj), "x");
            var body = Expression.Convert(Expression.Property(param, prop), typeof(object));
            return Expression.Lambda<Func<TObj, object?>>(body, param).Compile();
        }

        private static Action<TObj, object?> CompileSetter<TObj>(PropertyInfo prop)
        {
            var target = Expression.Parameter(typeof(TObj), "target");
            var value = Expression.Parameter(typeof(object), "value");
            var body = Expression.Assign(
                Expression.Property(target, prop),
                Expression.Convert(value, prop.PropertyType));

            return Expression.Lambda<Action<TObj, object?>>(body, target, value).Compile();
        }
    }
}
