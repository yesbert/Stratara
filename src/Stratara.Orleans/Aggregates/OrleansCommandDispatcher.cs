using System.Text.Json;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Shared.Outbox.Mapping;
using Stratara.Shared.Reflections;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// The durable-intent shape behind <c>ICommandOutboxDispatcher</c>: the command is recorded in
/// durable storage before the call returns, then handed to its grain without waiting for the
/// handler. The grain deletes the record once the handler has completed. A record still present
/// after <see cref="OrleansDispatchOptions.IntentGrace"/> is one whose hand-off was lost — a host
/// that died between recording and completing — and the drain resumes it by handing it over again.
/// </summary>
/// <remarks>
/// This keeps the interface's promise the same way the bus does — what is accepted is stored — and
/// changes what "accepted" means for a host that dies: the bus loses the message it had not yet
/// published, this loses nothing and may run a handler twice. Handlers are already expected to
/// tolerate a second delivery. While a replay is active, commands are recorded and not handed over,
/// as the bus dispatcher does. Hand-overs to one aggregate from one scope keep their order.
/// </remarks>
internal sealed class OrleansCommandDispatcher(
    IWriteUnitOfWork unitOfWork,
    IGrainFactory grainFactory,
    ISessionContextProvider sessionContextProvider,
    ISecureJsonSerializer serializer,
    IProjectionReplayState replayState,
    AggregateSendLane lane,
    IOptions<OrleansDispatchOptions> options,
    TimeProvider timeProvider) : ICommandOutboxDispatcher
{
    private readonly TimeSpan _grace = options.Value.IntentGrace;

    /// <inheritdoc/>
    public async Task<Guid> EnqueueCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : ICommand
    {
        var session = sessionContextProvider.Current ?? throw new InvalidOperationException("Session context is not set");
        var intentId = Guid.CreateVersion7();
        var heavy = command is IHeavyCommand;
        var aggregateId = (command as IAggregateScopedCommand)?.AggregateId;

        var recorded = RecordAsync(intentId, command, session, heavy, cancellationToken);
        var issued = lane.SendAsync(aggregateId ?? intentId, recorded, payload =>
            replayState.IsReplayActive ? Task.CompletedTask : HandOver(intentId, payload, heavy, aggregateId));

        (await issued).Ignore();
        return intentId;
    }

    /// <inheritdoc/>
    public async Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default)
    {
        if (replayState.IsReplayActive)
        {
            return;
        }

        var cutoff = timeProvider.GetUtcNow() - _grace;
        foreach (var entry in outboxEntries.Where(entry => entry.Timestamp <= cutoff))
        {
            var envelope = entry.MapTo<CommandEnvelope>();
            var payload = new AggregateCommandEnvelope(envelope.CommandTypeName, envelope.CommandJson, envelope.SessionContextJson);
            await HandOver(entry.Id, payload, envelope.Heavy, AggregateIdOf(envelope));
        }
    }

    private async Task<AggregateCommandEnvelope> RecordAsync<T>(Guid intentId, T command, SessionContext session, bool heavy, CancellationToken cancellationToken)
        where T : ICommand
    {
        var envelope = new CommandEnvelope(
            intentId,
            await serializer.SerializeAsync(command, session.TenantId, session.ActorUserId, cancellationToken),
            command.GetType().GetQualifiedTypeName(),
            JsonSerializer.Serialize(session),
            Heavy: heavy);

        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        await unitOfWork.CreateOutboxRepository(transaction).AddAsync(envelope, cancellationToken);
        await transaction.SaveChangesAsync(cancellationToken);

        return new AggregateCommandEnvelope(envelope.CommandTypeName, envelope.CommandJson, envelope.SessionContextJson);
    }

    private Task HandOver(Guid intentId, AggregateCommandEnvelope payload, bool heavy, Guid? aggregateId)
    {
        if (heavy)
        {
            return grainFactory.GetGrain<IHeavyWorkGrain>(0).ExecuteIntentAsync(intentId, payload);
        }

        return aggregateId is { } id
            ? grainFactory.GetGrain<IAggregateGrain>(id).ExecuteIntentAsync(intentId, payload)
            : grainFactory.GetGrain<ICommandRunnerGrain>(intentId).ExecuteIntentAsync(payload);
    }

    /// <summary>
    /// A resumed intent has only its JSON; the aggregate id is read back from it by the property
    /// name every aggregate-scoped command carries.
    /// </summary>
    private static Guid? AggregateIdOf(CommandEnvelope envelope)
    {
        using var document = JsonDocument.Parse(envelope.CommandJson);
        return document.RootElement.TryGetProperty(nameof(IAggregateScopedCommand.AggregateId), out var property)
               && property.TryGetGuid(out var aggregateId)
            ? aggregateId
            : null;
    }
}

/// <summary>Settings for the Orleans-backed command dispatcher.</summary>
public sealed class OrleansDispatchOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Orleans:Dispatch";

    /// <summary>
    /// How long a recorded intent is assumed to be in flight before the drain hands it over again.
    /// Shorter than the slowest handler means that handler may run twice.
    /// </summary>
    public TimeSpan IntentGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long completed intents are collected before one delete removes them. A host that dies
    /// inside the window resumes those intents after <see cref="IntentGrace"/> and runs their handlers
    /// a second time; keep it far below the grace.
    /// </summary>
    public TimeSpan CompletionWindow { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>How many completed intents one delete removes at most; a full batch ends the window early.</summary>
    public int CompletionBatchSize { get; set; } = 64;
}
