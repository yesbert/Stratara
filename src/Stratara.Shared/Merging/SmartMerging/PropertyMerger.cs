using Stratara.Shared.Reflections;

namespace Stratara.Shared.Merging.SmartMerging;

/// <summary>
/// Internal contract for the per-property merge step used by <see cref="SmartMerger{TCommand,TAggregate}"/>.
/// </summary>
/// <typeparam name="TCommand">CLR type of the update command.</typeparam>
/// <typeparam name="TAggregate">CLR type of the aggregate / read model.</typeparam>
internal interface IPropertyMerger<in TCommand, in TAggregate>
{
    /// <summary>Applies the merge rule to a single property and writes the resolved value into <paramref name="result"/>.</summary>
    /// <param name="command">The incoming command authored against <paramref name="source"/>.</param>
    /// <param name="source">The aggregate state the caller observed when authoring the command.</param>
    /// <param name="current">The live aggregate state.</param>
    /// <param name="result">Destination instance receiving the merged property value.</param>
    void Merge(TCommand command, TAggregate source, TAggregate current, TCommand result);
}

/// <summary>
/// Per-property smart-merge implementation. Compares the command's property value against the
/// caller-observed source value; if they match the current aggregate value wins (avoiding lost
/// updates), otherwise the command's value wins.
/// </summary>
/// <typeparam name="TCommand">CLR type of the update command.</typeparam>
/// <typeparam name="TAggregate">CLR type of the aggregate / read model.</typeparam>
/// <typeparam name="TProp">CLR type of the property being merged.</typeparam>
internal sealed class PropertyMerger<TCommand, TAggregate, TProp>(string propertyName) : IPropertyMerger<TCommand, TAggregate> // NOSONAR — 3 type parameters are necessary: TCommand, TAggregate and TProp (property type) each serve a distinct generic role
{
    private readonly Func<TAggregate, TProp> _getAggregate = PropertyAccessorCache.GetOrCreateGetter<TAggregate, TProp>(propertyName);
    private readonly Func<TCommand, TProp> _getCommand = PropertyAccessorCache.GetOrCreateGetter<TCommand, TProp>(propertyName);
    private readonly Action<TCommand, TProp> _setCommand = PropertyAccessorCache.GetOrCreateSetter<TCommand, TProp>(propertyName);

    /// <inheritdoc/>
    public void Merge(TCommand command, TAggregate source, TAggregate current, TCommand result)
    {
        var commandValue = _getCommand(command);
        var sourceValue = _getAggregate(source);
        var currentValue = _getAggregate(current);

        if (EqualityComparer<TProp>.Default.Equals(commandValue, sourceValue))
        {
            _setCommand(result, currentValue);
            return;
        }

        _setCommand(result, commandValue);
    }
}
