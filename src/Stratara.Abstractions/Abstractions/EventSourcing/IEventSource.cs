namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Write-side façade for the event store. Command handlers append events through this
/// service; the implementation tracks pending writes and flushes them on
/// <see cref="SaveChangesAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every event is recorded under the tenant that owns it — its Subject — and that tenant is not
/// simply the one in the caller's session. For each event the store takes the first of these that
/// names a tenant: the Subject stated for that event with
/// <see cref="AppendOnBehalfOfAsync{TAggregate}"/>; the owner already resolved for the same stream
/// earlier in the batch; the owner recorded on the stream's first event — its tenant, and its user
/// where one was recorded; the <see cref="IAggregateCreationEvent.TenantId"/> of a creation event;
/// and only then the tenant in the session. If none of them names a tenant, the append fails.
/// </para>
/// <para>
/// A stream therefore keeps the owner it was created with, user included, whatever session appends
/// to it later; a stream whose first event names no user is given none. A first event that states no owner takes the tenant in the session, so an aggregate created for
/// another tenant states that tenant on its first event by implementing
/// <see cref="IAggregateCreationEvent"/>.
/// </para>
/// </remarks>
/// <example>
/// Append events from a command handler and commit the unit-of-work:
/// <code>
/// public sealed class CreateOrderHandler(IEventSource events) : ICommandHandler&lt;CreateOrder&gt;
/// {
///     public async Task HandleAsync(CreateOrder command, CancellationToken cancellationToken)
///     {
///         await events.CreateAsync&lt;Order&gt;(command.OrderId,
///             new OrderCreated(command.OrderId, command.CustomerId, command.Amount),
///             cancellationToken);
///         await events.SaveChangesAsync(cancellationToken);
///     }
/// }
/// </code>
/// </example>
public interface IEventSource
{
    /// <summary>Returns <c>true</c> if the stream exists in the event store.</summary>
    Task<bool> ExistsAsync(Guid streamId, CancellationToken cancellationToken = default);

    /// <summary>Returns the head version of the stream, or <c>0</c> if it does not exist.</summary>
    Task<long> GetCurrentVersionAsync(Guid streamId, CancellationToken cancellationToken = default);

    /// <summary>Create a new stream with the first event. Fails if the stream already exists.</summary>
    /// <remarks>
    /// The new stream's owner is the tenant the event carries when it is an
    /// <see cref="IAggregateCreationEvent"/> with a non-empty tenant, otherwise the tenant in the
    /// session. Every later event on the stream keeps that owner.
    /// </remarks>
    /// <typeparam name="TAggregate">The aggregate type the stream represents.</typeparam>
    /// <param name="streamId">The stream id.</param>
    /// <param name="event">
    /// The creation event. Implement <see cref="IAggregateCreationEvent"/> on it to state the new
    /// stream's owner instead of taking the tenant in the session.
    /// </param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    /// <exception cref="Stratara.Abstractions.Session.SessionRequiredException">No session context is set on the current scope.</exception>
    Task CreateAsync<TAggregate>(Guid streamId, object @event, CancellationToken cancellationToken = default)
        where TAggregate : notnull, new();

    /// <summary>Create a new stream with multiple events in order.</summary>
    /// <remarks>
    /// The owner is resolved from the first event as in <see cref="CreateAsync{TAggregate}"/>, and
    /// the events after it take the same owner.
    /// </remarks>
    /// <typeparam name="TAggregate">The aggregate type the stream represents.</typeparam>
    /// <param name="streamId">The stream id.</param>
    /// <param name="events">The events to append, in order.</param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    /// <exception cref="Stratara.Abstractions.Session.SessionRequiredException">No session context is set on the current scope.</exception>
    Task CreateRangeAsync<TAggregate>(Guid streamId, IEnumerable<object> events,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new();

    /// <summary>Append an event to an existing stream.</summary>
    /// <remarks>
    /// The event takes the owner recorded on the stream, not the tenant in the session. On a stream
    /// that does not exist yet, the owner is resolved as for the first event of
    /// <see cref="CreateAsync{TAggregate}"/>. Use <see cref="AppendOnBehalfOfAsync{TAggregate}"/> for
    /// an event whose owner differs from the stream's.
    /// </remarks>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="streamId">The stream id.</param>
    /// <param name="event">The event payload.</param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    /// <exception cref="Stratara.Abstractions.Session.SessionRequiredException">No session context is set on the current scope.</exception>
    Task AppendAsync<TAggregate>(Guid streamId, object @event, CancellationToken cancellationToken = default)
        where TAggregate : notnull, new();

    /// <summary>Append multiple events to an existing stream in order.</summary>
    /// <remarks>The events take the owner recorded on the stream, as in <see cref="AppendAsync{TAggregate}"/>.</remarks>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="streamId">The stream id.</param>
    /// <param name="events">The events to append, in order.</param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    /// <exception cref="Stratara.Abstractions.Session.SessionRequiredException">No session context is set on the current scope.</exception>
    Task AppendRangeAsync<TAggregate>(Guid streamId, IEnumerable<object> events,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new();

    /// <summary>
    /// Append an event with an explicit Subject (data owner), overriding every other source — the
    /// stream's recorded owner, a creation event's tenant and the session — for this one event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this method for an event whose owner differs from the stream's. The actor recorded with
    /// the event stays the caller's session; only the owner changes, and only for this event — the
    /// next append to the stream takes the stream's owner again, in the same batch as in a later one.
    /// On a stream's first event the stated owner is the one the stream records.
    /// </para>
    /// <para>
    /// The event's protected fields are encrypted under the stated owner's keys, so an erasure of
    /// the stream's owner does not reach them, and an erasure of the stated owner can leave the
    /// stream unable to rehydrate.
    /// </para>
    /// </remarks>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="streamId">The stream id.</param>
    /// <param name="event">The event payload.</param>
    /// <param name="subject">
    /// The data owner to record. Its tenant id must be non-empty: stating the Subject also states
    /// that the stream, the event and the session are not to be consulted, so an empty one fails the
    /// append rather than falling back to them.
    /// </param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    /// <exception cref="ArgumentException"><paramref name="subject"/> names no tenant.</exception>
    /// <exception cref="Stratara.Abstractions.Session.SessionRequiredException">No session context is set on the current scope.</exception>
    Task AppendOnBehalfOfAsync<TAggregate>(Guid streamId, object @event, EventSubject subject,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new();

    /// <summary>
    /// Flush every pending append/create to the underlying write store. Throws
    /// <see cref="ConcurrencyException"/> if another writer committed first.
    /// </summary>
    /// <remarks>
    /// A save clears what was staged whether it succeeds or fails. After a failure, append the events
    /// again before saving again, as after a <see cref="ConcurrencyException"/>: a second save with
    /// nothing appended writes nothing.
    /// </remarks>
    /// <exception cref="ConcurrencyException">Another writer beat this one to the stream's head version.</exception>
    /// <exception cref="Stratara.Abstractions.Session.SessionRequiredException">No session context is set on the current scope.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
