using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Shared.Diagnostics.Extensions;
using Stratara.Shared.Mediator.Mapping;
using Stratara.Shared.Outbox.Mapping;
using Stratara.Resilience;

namespace Stratara.Outbox.RabbitMQ.Outbox;

/// <summary>
/// Outbox-pattern <see cref="ICommandOutboxDispatcher"/> for the message-bus-backed deployment.
/// Maps an <c>ICommand</c> to a <c>CommandEnvelope</c>, attempts a direct publish on the fast-path,
/// and falls back to persisting the envelope in the outbox table when the bus is unreachable or a
/// projection replay is in progress.
/// </summary>
/// <remarks>
/// Delivery semantics are at-least-once: when the direct publish succeeds the envelope is never
/// stored; when it fails the envelope is committed to the outbox and republished by
/// <see cref="OutboxWorker"/>. Consumers must therefore handle duplicate command deliveries
/// (typically via the <c>CommandAudit</c> idempotency log).
/// </remarks>
[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "DI-resolved sealed internal dispatcher; primary-constructor parameters reflect intrinsic " +
                    "framework dependencies (logger, unit-of-work, bus, messaging identifier, session, pipeline, " +
                    "replay state, serializer, signer) and are not a hand-called API surface.")]
internal sealed class CommandOutboxDispatcher(
    ILogger<CommandOutboxDispatcher> logger,
    IWriteUnitOfWork unitOfWork,
    IMessageBus messageBus,
    IMessagingIdentifier messagingIdentifier,
    ISessionContextProvider sessionContextProvider,
    ResiliencePipelineProvider<string> pipelineProvider,
    IProjectionReplayState replayState,
    ISecureJsonSerializer serializer,
    IBusEnvelopeSigner? signer = null) : ICommandOutboxDispatcher
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceNames.CommandDispatcher);

    /// <inheritdoc/>
    public async Task<Guid> EnqueueCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : ICommand
    {
        var sessionContext = sessionContextProvider.Current ?? throw new InvalidOperationException("Session context is not set");
        var commandEnvelope = await command.MapToAsync(sessionContext, serializer, cancellationToken);
        if (signer is not null)
        {
            commandEnvelope = commandEnvelope with { Signature = signer.Sign(BusEnvelopeCanonical.Of(commandEnvelope)) };
        }
        if (!replayState.IsReplayActive && await TrySendCommandEnvelopeAsync(commandEnvelope, cancellationToken))
        {
            return commandEnvelope.Id;
        }

        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateOutboxRepository(transaction);

        await repository.AddAsync(commandEnvelope, cancellationToken);
        await transaction.SaveChangesAsync(cancellationToken);

        return commandEnvelope.Id;
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
            var commandEnvelope = outboxEntry.MapTo<CommandEnvelope>();
            if (await TrySendCommandEnvelopeAsync(commandEnvelope, cancellationToken))
            {
                await repository.DeleteAsync(outboxEntry.Id, cancellationToken);
            }
        }

        await transaction.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TrySendCommandEnvelopeAsync(CommandEnvelope commandEnvelope, CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.ExecuteAsync(
                static async (state, ct) =>
                {
                    await state.messageBus.PublishAsync(state.messagingIdentifier.CommandTopic, state.commandEnvelope, ct);
                }, (messageBus, messagingIdentifier, commandEnvelope), cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogCommandEnvelopeDispatchFailed(messagingIdentifier.CommandTopic, ex);
            return false;
        }
    }
}
