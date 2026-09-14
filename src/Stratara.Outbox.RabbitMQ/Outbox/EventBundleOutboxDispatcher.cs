using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Diagnostics;
using Stratara.Shared.Diagnostics.Extensions;
using Stratara.Shared.Outbox.Mapping;
using Stratara.Resilience;

namespace Stratara.Outbox.RabbitMQ.Outbox;

/// <summary>
/// Outbox-pattern <see cref="IEventBundleOutboxDispatcher"/> for the message-bus-backed deployment.
/// Attempts a direct publish of an <c>EventBundle</c> on the fast-path, and falls back to
/// persisting the bundle in the outbox table when the bus is unreachable or a projection
/// replay is in progress.
/// </summary>
/// <remarks>
/// Delivery semantics are at-least-once. Projection handlers must therefore be idempotent —
/// either by checkpointing the highest event version per stream or by deduplicating on the
/// event identifier. Delivery order across consumers is not guaranteed: a subscription with
/// several consumers may process two consecutive bundles at the same time and in either order.
/// What a handler may rely on is the projection and saga workers' guarantee that bundles about
/// one aggregate are applied one at a time within a process, and the retry they give a handler
/// that throws <see cref="Stratara.Abstractions.EventSourcing.PrecedingFactMissingException"/>.
/// </remarks>
/// <remarks>
/// With <see cref="OutboxOptions.DurableBundles"/> the dispatcher stores the bundle under the
/// event source's own transaction before the commit, publishes it after, and removes the stored
/// copy once the bus has accepted it — so a committed fact is in the table until the bus has it,
/// and a process that ends between the two leaves a row the outbox drain delivers. The stored
/// entry's identity is remembered per bundle instance, weakly, so a save that fails after storing
/// leaves nothing behind in memory once its bundle is gone.
/// </remarks>
internal sealed class EventBundleOutboxDispatcher(
    ILogger<EventBundleOutboxDispatcher> logger,
    IWriteUnitOfWork unitOfWork,
    IMessageBus messageBus,
    IMessagingIdentifier messagingIdentifier,
    ResiliencePipelineProvider<string> pipelineProvider,
    IProjectionReplayState replayState,
    IOptions<OutboxOptions> options) : IEventBundleOutboxDispatcher
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceNames.EventBundleDispatcher);
    private readonly bool _durableBundles = options.Value.DurableBundles;
    private readonly ConditionalWeakTable<EventBundle, StrongBox<Guid>> _stored = new();

    /// <inheritdoc/>
    public bool StoresBundlesWithCommit => _durableBundles;

    /// <inheritdoc/>
    public async Task StoreEventBundleAsync(EventBundle eventBundle, ITransaction transaction, CancellationToken cancellationToken = default)
    {
        var id = Guid.CreateVersion7();
        await unitOfWork.CreateOutboxRepository(transaction).AddAsync(id, eventBundle, cancellationToken);
        _stored.AddOrUpdate(eventBundle, new StrongBox<Guid>(id));
    }

    /// <inheritdoc/>
    public async Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default)
    {
        if (_stored.TryGetValue(eventBundle, out var storedId))
        {
            _stored.Remove(eventBundle);
            if (!replayState.IsReplayActive && await TrySendEventBundleAsync(eventBundle, cancellationToken))
            {
                await RemoveStoredAsync(storedId.Value, cancellationToken);
            }

            return;
        }

        if (!replayState.IsReplayActive && await TrySendEventBundleAsync(eventBundle, cancellationToken))
        {
            return;
        }

        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateOutboxRepository(transaction);
        await repository.AddAsync(eventBundle, cancellationToken);

        await transaction.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Removes a stored bundle the bus has accepted, on a context of its own — the delete runs at
    /// once, outside the save's transaction, which has committed. A removal that fails is logged,
    /// not thrown: the save has committed and the bundle is published, and the drain will publish
    /// the leftover row again, which at-least-once already requires every handler to tolerate. A
    /// drain that ran between the commit and this removal has already published the row and
    /// deleted it; the removal then affects nothing, and the bundle was delivered twice.
    /// </summary>
    private async Task RemoveStoredAsync(Guid outboxEntryId, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await unitOfWork.StartAsync(cancellationToken);
            await unitOfWork.CreateOutboxRepository(transaction).DeleteAsync(outboxEntryId, cancellationToken);
            await transaction.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDurableBundleRemovalFailed(outboxEntryId, ex);
        }
    }

    /// <inheritdoc/>
    public async Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default)
    {
        if (replayState.IsReplayActive)
        {
            return;
        }

        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateOutboxRepository(transaction);

        var published = 0;
        foreach (var outboxEntry in outboxEntries)
        {
            var eventBundle = outboxEntry.MapTo<EventBundle>();
            if (await TrySendEventBundleAsync(eventBundle, cancellationToken))
            {
                await repository.DeleteAsync(outboxEntry.Id, cancellationToken);
                published++;
            }
        }

        if (published > 0)
        {
            ApplicationDiagnostics.Metrics.OutboxEntriesPublished.Add(
                published,
                new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.OutboxKind, ApplicationDiagnostics.OutboxKinds.Event));
        }

        await transaction.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TrySendEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.ExecuteAsync(
                static async (state, ct) =>
                {
                    await state.messageBus.PublishAsync(state.messagingIdentifier.EventBundleTopic, state.eventBundle, ct);
                }, (messageBus, messagingIdentifier, eventBundle), cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogEventBundleDispatchFailed(messagingIdentifier.EventBundleTopic, ex);
            return false;
        }
    }
}
