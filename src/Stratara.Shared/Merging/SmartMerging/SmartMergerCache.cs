using System.Reflection;

namespace Stratara.Shared.Merging.SmartMerging;

/// <summary>
/// Per-type-combination cache of typed <see cref="IPropertyMerger{TCommand, TAggregate}"/> entries.
/// Built once on first access via reflection over the command's properties — only properties that
/// exist on both <typeparamref name="TCommand"/> and <typeparamref name="TAggregate"/> with
/// matching CLR types are included.
/// </summary>
/// <typeparam name="TCommand">CLR type of the update command.</typeparam>
/// <typeparam name="TAggregate">CLR type of the aggregate / read model.</typeparam>
internal static class SmartMergerCache<TCommand, TAggregate>
{
    private static readonly List<IPropertyMerger<TCommand, TAggregate>> s_cachedProperties = BuildCachedProperties();

    private static List<IPropertyMerger<TCommand, TAggregate>> BuildCachedProperties()
    {
        var result = new List<IPropertyMerger<TCommand, TAggregate>>();
        var cmdProps = typeof(TCommand).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var aggProps = typeof(TAggregate).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name);

        foreach (var cmdProp in cmdProps)
        {
            if (!cmdProp.CanRead || !cmdProp.CanWrite || !aggProps.TryGetValue(cmdProp.Name, out var aggProp))
            {
                continue;
            }

            if (cmdProp.PropertyType != aggProp.PropertyType)
            {
                continue;
            }

            result.Add(PropertyMergerFactory<TCommand, TAggregate>.Create(cmdProp.Name, cmdProp.PropertyType));
        }

        return result;
    }

    /// <summary>Returns the cached list of property mergers for the <typeparamref name="TCommand"/> / <typeparamref name="TAggregate"/> pair.</summary>
    /// <returns>List of property mergers built on first access.</returns>
    public static List<IPropertyMerger<TCommand, TAggregate>> GetOrAdd() => s_cachedProperties;
}
