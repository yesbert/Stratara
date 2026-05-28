using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
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
/// event identifier.
/// </remarks>
internal sealed class EventBundleOutboxDispatcher(
    ILogger<EventBundleOutboxDispatcher> logger,
    IWriteUnitOfWork unitOfWork,
    IMessageBus messageBus,
    IMessagingIdentifier messagingIdentifier,
    ResiliencePipelineProvider<string> pipelineProvider,
    IProjectionReplayState replayState) : IEventBundleOutboxDispatcher
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceNames.EventBundleDispatcher);

    /// <inheritdoc/>
    public async Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default)
    {
        if (!replayState.IsReplayActive && await TrySendEventBundleAsync(eventBundle, cancellationToken))
        {
            return;
        }

        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateOutboxRepository(transaction);
        await repository.AddAsync(eventBundle, cancellationToken);

        await transaction.SaveChangesAsync(cancellationToken);
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

        foreach (var outboxEntry in outboxEntries)
        {
            var eventBundle = outboxEntry.MapTo<EventBundle>();
            if (await TrySendEventBundleAsync(eventBundle, cancellationToken))
            {
                await repository.DeleteAsync(outboxEntry.Id, cancellationToken);
            }
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
