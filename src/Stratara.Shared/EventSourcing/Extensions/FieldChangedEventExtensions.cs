using Stratara.Shared.Reflections;

namespace Stratara.Shared.EventSourcing.Extensions;

/// <summary>
/// Extension helpers that apply a generic <see cref="FieldChangedEvent{TAggregate}"/> to its target
/// aggregate by reflecting the named property onto the live aggregate instance.
/// </summary>
public static class FieldChangedEventExtensions
{
    /// <summary>
    /// Applies a <see cref="FieldChangedEvent{TAggregate}"/> to <paramref name="aggregate"/> by
    /// invoking the cached property setter for <see cref="FieldChangedEvent{TAggregate}.PropertyName"/>.
    /// </summary>
    /// <typeparam name="TAggregate">CLR type of the target aggregate.</typeparam>
    /// <param name="aggregate">The aggregate instance whose property is being mutated.</param>
    /// <param name="event">The change event carrying property name and new value.</param>
    /// <exception cref="ArgumentException">Thrown when the property does not exist on <typeparamref name="TAggregate"/>.</exception>
    public static void ApplyPropertyChanged<TAggregate>(this TAggregate aggregate, FieldChangedEvent<TAggregate> @event)
        where TAggregate : notnull
    {
        PropertyAccessorCache.SetValueByName(aggregate, @event.PropertyName, @event.NewValue);
    }
}
