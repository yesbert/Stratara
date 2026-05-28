using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;

namespace Stratara.Abstractions.Outbox;

/// <summary>
/// Enqueues commands for asynchronous out-of-process execution by the command-handling
/// worker. Implementations first try direct publish (fast path) and fall back to the
/// outbox table if the bus is unreachable.
/// </summary>
/// <example>
/// Dispatch a fire-and-forget mutation from an API endpoint:
/// <code>
/// public sealed record PlaceOrder(Guid OrderId, Guid CustomerId, decimal Amount) : ICommand;
///
/// app.MapPost("/orders", async (PlaceOrder command, ICommandOutboxDispatcher dispatcher, CancellationToken ct) =>
/// {
///     var envelopeId = await dispatcher.EnqueueCommandAsync(command, ct);
///     return Results.Accepted($"/orders/{command.OrderId}", new { envelopeId });
/// });
/// </code>
/// </example>
public interface ICommandOutboxDispatcher
{
    /// <summary>Enqueue <paramref name="command"/> for asynchronous dispatch.</summary>
    /// <typeparam name="T">The concrete command type.</typeparam>
    /// <param name="command">The command to dispatch.</param>
    /// <param name="cancellationToken">Propagated to the bus / outbox write.</param>
    /// <returns>The id assigned to the envelope.</returns>
    Task<Guid> EnqueueCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : ICommand;

    /// <summary>
    /// Drain previously-persisted <paramref name="outboxEntries"/> by attempting to
    /// publish each one and deleting on success. Used by the outbox worker.
    /// </summary>
    Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default);
}
