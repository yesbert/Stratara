namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Thrown by <see cref="IEventSource.SaveChangesAsync"/> when the save committed its events but handing
/// their bundle on failed afterwards. The events are recorded; appending them again would record the
/// same facts a second time.
/// </summary>
/// <remarks>
/// Readers that consume bundles did not receive these events: a projection is repaired by a replay; a saga
/// that reacts to bundles has missed them, and nothing repairs that, which is why a host that cannot lose
/// a bundle stores bundles with the commit. Such a host does not raise it, because its bundle is recorded
/// in the same transaction as the events. It is raised whatever ended the handover, a cancellation
/// included. Nothing in the framework runs the work again because of it: the transports acknowledge the
/// message instead of delivering it again; on the Orleans execution model a recorded command is completed
/// instead of resumed, a store reader counts the entry as applied, and a durable timer as fired; and the
/// retry pipelines do not retry it.
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
            "bundle on failed. The events are recorded: do not append them again. Readers that consume bundles did not " +
            "receive them: replay the projections that do. A host that stores bundles with the commit cannot lose one.",
            innerException)
    {
        _streamIds = streamIds;
        EventCount = eventCount;
    }

    private readonly IReadOnlyList<Guid>? _streamIds;

    /// <summary>
    /// The streams whose events the save committed; empty on an exception that crossed a process boundary, where
    /// only its type, message and inner exception are carried — the message names the streams too.
    /// </summary>
    public IReadOnlyList<Guid> StreamIds => _streamIds ?? [];

    /// <summary>How many events the save committed; zero on an exception that crossed a process boundary.</summary>
    public int EventCount { get; }
}
