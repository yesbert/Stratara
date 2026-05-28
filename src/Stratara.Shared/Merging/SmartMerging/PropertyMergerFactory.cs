namespace Stratara.Shared.Merging.SmartMerging;

/// <summary>
/// Factory that constructs a closed <see cref="PropertyMerger{TCommand, TAggregate, TProp}"/> for a
/// runtime property type. Bridges between the type-erased cache loop in
/// <see cref="SmartMergerCache{TCommand, TAggregate}"/> and the strongly-typed merger.
/// </summary>
/// <typeparam name="TCommand">CLR type of the update command.</typeparam>
/// <typeparam name="TAggregate">CLR type of the aggregate / read model.</typeparam>
internal static class PropertyMergerFactory<TCommand, TAggregate>
{
    /// <summary>
    /// Constructs an <see cref="IPropertyMerger{TCommand, TAggregate}"/> for the named property of
    /// CLR type <paramref name="propertyType"/>.
    /// </summary>
    /// <param name="propertyName">Public instance property name on both command and aggregate.</param>
    /// <param name="propertyType">CLR type of that property.</param>
    /// <returns>A typed property merger ready to plug into <see cref="SmartMerger{TCommand, TAggregate}"/>.</returns>
    public static IPropertyMerger<TCommand, TAggregate> Create(string propertyName, Type propertyType)
    {
        var mergerType = typeof(PropertyMerger<,,>).MakeGenericType(typeof(TCommand), typeof(TAggregate), propertyType);
        return (IPropertyMerger<TCommand, TAggregate>)Activator.CreateInstance(mergerType, propertyName)!;
    }
}
