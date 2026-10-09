using System.Diagnostics.CodeAnalysis;

namespace Stratara.Shared.EventSourcing;

/// <summary>
/// Write-side event-sourcing settings, bound to the <c>EventSourcing</c> configuration section.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class EventSourcingOptions
{
    /// <summary>Configuration section name (<c>EventSourcing</c>) that hosts these options.</summary>
    public const string SectionName = "EventSourcing";

    /// <summary>
    /// Gets or sets whether an append to a stream that was rebuilt through the aggregation service in
    /// the same unit of work is made on the condition that the stream is still at the version that
    /// rebuild saw. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handler reads a stream, decides on what it read, and appends. Without the condition the
    /// append is numbered after whatever the stream holds when it is made, so a write that landed
    /// between the read and the append goes unnoticed and the handler's fact is recorded after it.
    /// With the condition the save fails with a concurrency conflict instead, which the bus and the
    /// Orleans execution model redeliver: the handler runs again and decides on the stream's current
    /// state.
    /// </para>
    /// <para>
    /// The condition is the version of the first such rebuild of the stream; a later rebuild of the same
    /// stream in the unit of work does not move it. A rebuild bounded to an earlier version sets no
    /// condition; a rebuild of a stream that does not exist sets the condition that it still does not. A
    /// successful save ends the condition on the streams it wrote; a failed save ends it on every stream.
    /// The cost is that a handler appending to a stream another writer touches concurrently now sees
    /// a conflict where it used to succeed, and on the mediator path that conflict reaches the caller.
    /// </para>
    /// </remarks>
    public bool AppendAgainstAggregatedVersion { get; set; }
}
