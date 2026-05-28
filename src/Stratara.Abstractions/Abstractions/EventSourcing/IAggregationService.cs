namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Reconstructs an aggregate by reading its event stream + applying events. Command
/// handlers and sagas use this — never read aggregates from projection views.
/// </summary>
/// <remarks>
/// Tenant ownership: handlers operating on <c>ITenantAggregate</c>s should prefer the
/// <see cref="AggregationServiceTenantExtensions.AggregateOwnedByTenantAsync{TAggregate}"/>
/// extension, which returns <c>null</c> on either "not found" or "wrong tenant" — the
/// handler can then map both to <c>NotFound</c> without leaking foreign-tenant existence.
/// </remarks>
/// <example>
/// Rehydrate an aggregate in a command handler, then apply a mutation:
/// <code>
/// public sealed class ShipOrderHandler(IAggregationService aggregator, IEventSource events)
///     : ICommandHandler&lt;ShipOrder&gt;
/// {
///     public async Task HandleAsync(ShipOrder command, CancellationToken cancellationToken)
///     {
///         var order = await aggregator.AggregateAsync&lt;Order&gt;(command.OrderId, cancellationToken: cancellationToken)
///             ?? throw new InvalidOperationException($"Order {command.OrderId} not found.");
///
///         await events.AppendAsync&lt;Order&gt;(order.Id, new OrderShipped(order.Id, DateTimeOffset.UtcNow),
///             cancellationToken);
///         await events.SaveChangesAsync(cancellationToken);
///     }
/// }
/// </code>
/// </example>
public interface IAggregationService
{
    /// <summary>
    /// Reconstruct an aggregate of <typeparamref name="TAggregate"/> from
    /// <paramref name="streamId"/>'s event stream.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type — must have a parameterless constructor.</typeparam>
    /// <param name="streamId">The aggregate's stream id.</param>
    /// <param name="fromVersion">Inclusive start version, or <c>null</c> for stream start.</param>
    /// <param name="toVersion">Inclusive end version, or <c>null</c> for stream head.</param>
    /// <param name="cancellationToken">Propagated to the read-store query.</param>
    /// <returns>The reconstructed aggregate, or <c>null</c> if the stream does not exist.</returns>
    Task<TAggregate?> AggregateAsync<TAggregate>(Guid streamId, long? fromVersion = null, long? toVersion = null,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new();

    /// <summary>
    /// Reflection-based variant for when the aggregate type is known only at runtime
    /// (replay tooling, generic admin endpoints).
    /// </summary>
    /// <param name="aggregateType">The aggregate type to construct.</param>
    /// <param name="streamId">The aggregate's stream id.</param>
    /// <param name="fromVersion">Inclusive start version, or <c>null</c> for stream start.</param>
    /// <param name="toVersion">Inclusive end version, or <c>null</c> for stream head.</param>
    /// <param name="cancellationToken">Propagated to the read-store query.</param>
    /// <returns>The reconstructed aggregate, or <c>null</c> if the stream does not exist.</returns>
    Task<object?> AggregateAsync(Type aggregateType, Guid streamId, long? fromVersion = null, long? toVersion = null,
        CancellationToken cancellationToken = default);
}
