using System.Diagnostics.CodeAnalysis;

namespace Stratara.Shared.EventSourcing;

/// <summary>
/// Strongly-typed options object bound to the <c>EventSourcing</c> configuration section. Currently
/// acts as a marker type for <c>IOptions&lt;EventSourcingOptions&gt;</c> registrations; subkeys are
/// populated by downstream packages (e.g. snapshot intervals, hashing cadence).
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class EventSourcingOptions // NOSONAR — used as generic type parameter in AddOptions<EventSourcingOptions>(); cannot be static
{
    /// <summary>Configuration section name (<c>EventSourcing</c>) that hosts these options.</summary>
    public const string SectionName = "EventSourcing";
}
