using Stratara.Abstractions.Commands;
using Stratara.Abstractions.Merging.ChangeTracking;

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Diffs an in-flight update-command against the current aggregate state to produce a
/// minimal change-set, then applies the change-set by emitting one field-change event
/// per modified property. Used by the framework's generic update pipeline.
/// </summary>
public interface IChangeSetHandler
{
    /// <summary>
    /// Compute the change-set between <paramref name="command"/> and the current state
    /// of <typeparamref name="TAggregate"/>.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate the command targets.</typeparam>
    /// <typeparam name="TUpdateCommand">The concrete update-command type.</typeparam>
    /// <param name="command">The update command instance.</param>
    /// <param name="cancellationToken">Propagated to the aggregate read.</param>
    /// <returns>One <see cref="ChangeDetail"/> per modified property; empty if nothing changed.</returns>
    Task<IReadOnlyList<ChangeDetail>> CreateChangeSetAsync<TAggregate, TUpdateCommand>(TUpdateCommand command,
        CancellationToken cancellationToken = default)
        where TAggregate : class, new()
        where TUpdateCommand : IUpdateCommand;

    /// <summary>
    /// Apply a previously-computed change-set by appending one field-change event per
    /// entry to the aggregate's event stream.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate the change-set targets.</typeparam>
    /// <param name="aggregateId">The aggregate's stream id.</param>
    /// <param name="changeSet">The change-set produced by <see cref="CreateChangeSetAsync"/>.</param>
    /// <param name="cancellationToken">Propagated to the event-store write.</param>
    Task ApplyChangeSetAsync<TAggregate>(
        Guid aggregateId,
        IReadOnlyList<ChangeDetail> changeSet,
        CancellationToken cancellationToken = default)
        where TAggregate : class, new();
}
