using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Commands;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Merging.ChangeTracking;
using Stratara.Shared.Diagnostics.Extensions;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.Merging.ChangeTracking;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Diff-and-apply implementation of <see cref="IChangeSetHandler"/>: rebuilds the aggregate at the
/// caller's source version and at the current version, then emits one
/// <see cref="FieldChangedEvent{TAggregate}"/> per changed property when applying the change set.
/// </summary>
/// <remarks>
/// The handler enables optimistic-concurrency-aware updates: if another writer mutated the same
/// aggregate between read and write, the per-field events still merge cleanly because each event
/// targets exactly one property.
/// </remarks>
internal sealed class ChangeSetHandler(ILogger<ChangeSetHandler> logger, IEventSource eventSource, IAggregationService aggregationService) : IChangeSetHandler
{
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the aggregate identified by <c>command.AggregateId</c> does not exist (current or source version).
    /// </exception>
    public async Task<IReadOnlyList<ChangeDetail>> CreateChangeSetAsync<TAggregate, TUpdateCommand>(
        TUpdateCommand command,
        CancellationToken cancellationToken = default)
        where TAggregate : class, new() where TUpdateCommand : IUpdateCommand
    {
        var sourceAggregate = await aggregationService.AggregateAsync<TAggregate>(command.AggregateId,
            toVersion: command.SourceVersion,
            cancellationToken: cancellationToken);
        var currentAggregate =
            await aggregationService.AggregateAsync<TAggregate>(command.AggregateId, cancellationToken: cancellationToken);
        if (sourceAggregate is null || currentAggregate is null)
        {
            logger.LogChangeSetNotFound(typeof(TAggregate).Name, command.AggregateId);
            throw new InvalidOperationException(
                $"Aggregate of type {typeof(TAggregate).Name} with ID {command.AggregateId} not found in the event store.");
        }

        var changeSet =
            ChangeSetBuilder<TAggregate, TUpdateCommand>.CreateChangeSet(sourceAggregate, currentAggregate, command);

        logger.LogChangeSetCreated(command.AggregateId, changeSet);
        return changeSet;
    }

    /// <inheritdoc/>
    public async Task ApplyChangeSetAsync<TAggregate>(Guid aggregateId, IReadOnlyList<ChangeDetail> changeSet,
        CancellationToken cancellationToken = default) where TAggregate : class, new()
    {
        if (changeSet.Count == 0)
        {
            logger.LogNoChangesToApplied(aggregateId);
            return;
        }

        foreach (var changeDetail in changeSet)
        {
            await eventSource.AppendAsync<TAggregate>(aggregateId,
                new FieldChangedEvent<TAggregate>(changeDetail.PropertyName, changeDetail.ChangeValue), cancellationToken);
        }

        logger.LogChangeSetApplied(aggregateId);
        await eventSource.SaveChangesAsync(cancellationToken);
    }
}
