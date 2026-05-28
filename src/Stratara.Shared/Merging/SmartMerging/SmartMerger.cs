namespace Stratara.Shared.Merging.SmartMerging;

/// <summary>
/// Property-by-property smart-merge of an incoming <typeparamref name="TCommand"/> against the live
/// <typeparamref name="TAggregate"/> state. For each property that exists on both types with a
/// matching CLR type, the value wins as follows: if the command's value equals the source aggregate
/// value the caller observed (no intent to change), the current live value wins; otherwise the
/// command's value wins.
/// </summary>
/// <typeparam name="TCommand">CLR type of the update command. Must be parameterless-constructable so the merger can create a fresh result.</typeparam>
/// <typeparam name="TAggregate">CLR type of the aggregate / read model.</typeparam>
/// <remarks>
/// Property accessors are compiled and cached per-type-combination via
/// <see cref="Reflections.PropertyAccessorCache"/>. Properties that do not exist on both types or
/// whose CLR types differ are ignored.
/// </remarks>
public static class SmartMerger<TCommand, TAggregate> where TCommand : new()
{
    private static readonly List<IPropertyMerger<TCommand, TAggregate>> s_properties = SmartMergerCache<TCommand, TAggregate>.GetOrAdd();

    /// <summary>
    /// Returns a fresh <typeparamref name="TCommand"/> with each property resolved by the
    /// smart-merge rules described on <see cref="SmartMerger{TCommand,TAggregate}"/>.
    /// </summary>
    /// <param name="originalCommand">The incoming command authored against <paramref name="source"/>.</param>
    /// <param name="source">The aggregate state the caller observed when authoring the command.</param>
    /// <param name="current">The live aggregate state to merge against.</param>
    /// <returns>A new <typeparamref name="TCommand"/> with the merged property values.</returns>
    public static TCommand Merge(TCommand originalCommand, TAggregate source, TAggregate current)
    {
        var resultCommand = new TCommand();

        foreach (var prop in s_properties)
        {
            prop.Merge(originalCommand, source, current, resultCommand);
        }

        return resultCommand;
    }
}
