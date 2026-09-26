namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Which recorded events a reader has a use for, so the event mapper can leave every other event
/// unread: not resolved against the trusted types and not decrypted.
/// </summary>
/// <remarks>
/// <para>
/// A projection or a stateless saga is dispatched events by their exact type, so its host's relevance is
/// <see cref="ForTypes"/> of the types its handlers take. An event whose type, after upcasting, resolves
/// to one of them is mapped; one that resolves to another type is skipped. An event whose type does not
/// resolve is skipped too — unless its name, without namespace or assembly, is the name of a relevant
/// type, in which case it is mapped and fails as an unregistered type does, so a handled type moved
/// without an upcaster is reported rather than lost.
/// </para>
/// <para>
/// A stateful saga process decides from the event itself whether it handles it, so its reader uses
/// <see cref="AnyResolvable"/>: every event whose type resolves is mapped, and one whose type does not is
/// skipped.
/// </para>
/// </remarks>
public sealed class EventRelevance
{
    private readonly HashSet<Type>? _types;
    private readonly HashSet<string> _typeNames;

    private EventRelevance(HashSet<Type>? types)
    {
        _types = types;
        _typeNames = types is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(types.Select(t => t.Name), StringComparer.Ordinal);
    }

    /// <summary>Every event whose type resolves is relevant; none whose type does not.</summary>
    public static EventRelevance AnyResolvable { get; } = new(null);

    /// <summary>Whether this relevance takes every resolvable type rather than a set of them.</summary>
    public bool IsAnyResolvable => _types is null;

    /// <summary>The events of exactly <paramref name="types"/> are relevant.</summary>
    /// <param name="types">The event payload types a reader's handlers take.</param>
    /// <returns>The relevance of those types.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="types"/> is <see langword="null"/>.</exception>
    public static EventRelevance ForTypes(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        return new EventRelevance([.. types]);
    }

    /// <summary>Whether an event of <paramref name="eventType"/> is relevant.</summary>
    /// <param name="eventType">The event's payload type, as resolved after upcasting.</param>
    /// <returns><see langword="true"/> when the event is to be mapped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eventType"/> is <see langword="null"/>.</exception>
    public bool Includes(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return _types is null || _types.Contains(eventType);
    }

    /// <summary>
    /// Whether an event whose type does not resolve could still be a relevant one: its type name, without
    /// namespace or assembly, is the name of a relevant type.
    /// </summary>
    /// <param name="typeName">The simple type name, without namespace, declaring type or assembly.</param>
    /// <returns><see langword="true"/> when such an event is to be mapped, so it fails as an unregistered type does.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="typeName"/> is <see langword="null"/>.</exception>
    public bool MayName(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return _typeNames.Contains(typeName);
    }
}
