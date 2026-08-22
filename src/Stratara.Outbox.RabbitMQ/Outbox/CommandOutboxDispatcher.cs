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
using Stratara.Abstractions.Reflections;
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
                    "replay state, serializer, signer, trusted-type resolver) and are not a hand-called API surface.")]
internal sealed class CommandOutboxDispatcher(
    ILogger<CommandOutboxDispatcher> logger,
    IWriteUnitOfWork unitOfWork,
    IMessageBus messageBus,
    IMessagingIdentifier messagingIdentifier,
    ISessionContextProvider sessionContextProvider,
    ResiliencePipelineProvider<string> pipelineProvider,
    IProjectionReplayState replayState,
    ISecureJsonSerializer serializer,
    IBusEnvelopeSigner? signer = null,
    ITrustedTypeResolver? typeResolver = null) : ICommandOutboxDispatcher
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
        var topic = messagingIdentifier.GetCommandTopic(command.GetType());
        if (!replayState.IsReplayActive && await TrySendCommandEnvelopeAsync(commandEnvelope, topic, cancellationToken))
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
            var topic = ResolveTopic(commandEnvelope);
            if (await TrySendCommandEnvelopeAsync(commandEnvelope, topic, cancellationToken))
            {
                await repository.DeleteAsync(outboxEntry.Id, cancellationToken);
            }
        }

        await transaction.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TrySendCommandEnvelopeAsync(CommandEnvelope commandEnvelope, string topic, CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.ExecuteAsync(
                static async (state, ct) =>
                {
                    await state.messageBus.PublishAsync(state.topic, state.commandEnvelope, ct);
                }, (messageBus, topic, commandEnvelope), cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogCommandEnvelopeDispatchFailed(topic, ex);
            return false;
        }
    }

    /// <summary>
    /// Resolves the command topic for a stored envelope. The lane the command declared at enqueue time
    /// travels with the envelope, so a heavy command republished from durable storage reaches the heavy
    /// topic even when its type cannot be resolved in this process. An envelope written before the lane
    /// was recorded carries no flag; for those the command type is resolved as before, and only when that
    /// also fails does the entry fall back to the default topic so the outbox drains rather than stalling.
    /// </summary>
    private string ResolveTopic(CommandEnvelope commandEnvelope)
    {
        if (commandEnvelope.Heavy)
        {
            return messagingIdentifier.HeavyCommandTopic;
        }

        if (typeResolver is not null && typeResolver.TryResolve(commandEnvelope.CommandTypeName, out var type) && type is not null)
        {
            return messagingIdentifier.GetCommandTopic(type);
        }

        return messagingIdentifier.CommandTopic;
    }
}
