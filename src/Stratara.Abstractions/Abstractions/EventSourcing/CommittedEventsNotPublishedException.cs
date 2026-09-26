namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Thrown by <see cref="IEventSource.SaveChangesAsync"/> when the save committed its events but handing
/// their bundle on failed afterwards. The events are recorded; appending them again would record the
/// same facts a second time.
/// </summary>
/// <remarks>
/// Readers that consume bundles do not see these events until they are republished or replayed. A host
/// that stores bundles with the commit does not raise it, because its bundle is recorded in the same
/// transaction as the events. The framework's retry pipelines do not retry it.
/// </remarks>
public sealed class CommittedEventsNotPublishedException : Exception
{
    /// <summary>Initialise a new <see cref="CommittedEventsNotPublishedException"/>.</summary>
    /// <param name="streamIds">The streams whose events the save committed.</param>
    /// <param name="eventCount">How many events the save committed.</param>
    /// <param name="innerException">The failure handing the bundle on raised.</param>
    public CommittedEventsNotPublishedException(IReadOnlyList<Guid> streamIds, int eventCount, Exception innerException)
        : base(
            $"The save committed {eventCount} event(s) on stream(s) {string.Join(", ", streamIds)}, but handing their " +
            "bundle on failed. The events are recorded: do not append them again. Readers that consume bundles see " +
            "them only once they are republished or replayed.",
            innerException)
    {
        StreamIds = streamIds;
        EventCount = eventCount;
    }

    /// <summary>The streams whose events the save committed.</summary>
    public IReadOnlyList<Guid> StreamIds { get; }

    /// <summary>How many events the save committed.</summary>
    public int EventCount { get; }
}
