namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Write-side façade for the event store. Command handlers append events through this
/// service; the implementation tracks pending writes and flushes them on
/// <see cref="SaveChangesAsync"/>.
/// </summary>
/// <remarks>
/// All <c>AppendAsync</c> / <c>CreateAsync</c> calls infer the Subject (data-owner) from
/// the ambient <c>ISessionContextProvider</c>. Use
/// <see cref="AppendOnBehalfOfAsync{TAggregate}"/> when the Subject must be overridden
/// (PlatformAdmin cross-tenant flows, EventStoreMigration regeneration).
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
    /// <typeparam name="TAggregate">The aggregate type the stream represents.</typeparam>
    /// <param name="streamId">The stream id.</param>
    /// <param name="event">The creation event (typically an <see cref="IAggregateCreationEvent"/>).</param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    Task CreateAsync<TAggregate>(Guid streamId, object @event, CancellationToken cancellationToken = default)
        where TAggregate : notnull, new();

    /// <summary>Create a new stream with multiple events in order.</summary>
    /// <typeparam name="TAggregate">The aggregate type the stream represents.</typeparam>
    /// <param name="streamId">The stream id.</param>
    /// <param name="events">The events to append, in order.</param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    Task CreateRangeAsync<TAggregate>(Guid streamId, IEnumerable<object> events,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new();

    /// <summary>Append an event to an existing stream.</summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="streamId">The stream id.</param>
    /// <param name="event">The event payload.</param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    Task AppendAsync<TAggregate>(Guid streamId, object @event, CancellationToken cancellationToken = default)
        where TAggregate : notnull, new();

    /// <summary>Append multiple events to an existing stream in order.</summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="streamId">The stream id.</param>
    /// <param name="events">The events to append, in order.</param>
    /// <param name="cancellationToken">Propagated to the write-store transaction.</param>
    Task AppendRangeAsync<TAggregate>(Guid streamId, IEnumerable<object> events,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new();

    /// <summary>
    /// Append an event with an explicit Subject (data owner), overriding the
    /// SessionContext-derived Subject. Used by PlatformAdmin cross-tenant flows
    /// and EventStoreMigration regeneration. The Actor stays the calling
    /// SessionContext's ActorTenantId/ActorUserId.
    /// </summary>
    Task AppendOnBehalfOfAsync<TAggregate>(Guid streamId, object @event, EventSubject subject,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new();

    /// <summary>
    /// Flush every pending append/create to the underlying write store. Throws
    /// <see cref="ConcurrencyException"/> if another writer committed first.
    /// </summary>
    /// <exception cref="ConcurrencyException">Another writer beat this one to the stream's head version.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
