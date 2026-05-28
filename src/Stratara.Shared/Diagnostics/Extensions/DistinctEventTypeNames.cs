using Stratara.Abstractions.EventSourcing;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Deferred-formatting wrapper around an <see cref="IReadOnlyList{IEvent}"/> for source-generated
/// logger extensions that surface a distinct, comma-separated list of <see cref="IEvent.EventTypeName"/>
/// values. The wrapping struct is zero-cost at construction; the projection + distinct + join only
/// run inside <see cref="ToString"/>, which the source-generated logger formatter invokes solely
/// when the target log channel is enabled — keeping the hot path allocation-free when Debug
/// logging is disabled.
/// </summary>
public readonly struct DistinctEventTypeNames
{
    private readonly IReadOnlyList<IEvent> _events;

    /// <summary>Initializes a new wrapper over the supplied event list.</summary>
    /// <param name="events">The events to render type-names from; must not be <see langword="null"/>.</param>
    public DistinctEventTypeNames(IReadOnlyList<IEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
    }

    /// <summary>Renders the wrapped event list as a comma-separated, distinct list of type names.</summary>
    /// <returns>A distinct projection of <see cref="IEvent.EventTypeName"/> across the wrapped events.</returns>
    public override string ToString() => string.Join(",", _events.Select(e => e.EventTypeName).Distinct());
}
