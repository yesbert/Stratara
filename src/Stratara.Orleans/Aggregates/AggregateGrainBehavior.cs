using System.Text.Json;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Shared.Reflections;
using IRequest = Stratara.Abstractions.Mediator.IRequest;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// The synchronous shape of the aggregate grain: a command that names an aggregate is forwarded to
/// that aggregate's grain and the caller waits for it to complete, so the caller sees the result of
/// its own command — including a concurrency conflict — the way an in-process dispatch would. Register
/// it last, so every other behaviour has run before the hand-off; inside the grain the handler is
/// invoked directly. A command for the aggregate whose turn is running is let through; a command a
/// handler sends for another aggregate is forwarded to that aggregate's grain.
/// </summary>
/// <remarks>
/// This does not implement <c>ICommandOutboxDispatcher</c>: a grain call is a remote call and is lost
/// with the caller if the host dies before the append. The durable-intent shape is the one that keeps
/// that interface's promise.
/// </remarks>
/// <typeparam name="TRequest">The command type the pipeline is running for.</typeparam>
internal sealed class AggregateGrainBehavior<TRequest>(
    IGrainFactory grainFactory,
    ISessionContextProvider sessionContextProvider,
    ISecureJsonSerializer serializer,
    AggregateSendLane lane) : IPipelineBehavior<TRequest>
    where TRequest : IRequest
{
    /// <inheritdoc/>
    public async Task HandleAsync(TRequest request, Func<Task> next, CancellationToken cancellationToken)
    {
        if (request is not IAggregateScopedCommand scoped || AggregateTurn.IsInside(scoped.AggregateId))
        {
            await next();
            return;
        }

        var session = sessionContextProvider.Current ?? throw new InvalidOperationException("Session context is not set");
        var grain = grainFactory.GetGrain<IAggregateGrain>(scoped.AggregateId);
        var call = await lane.SendAsync(scoped.AggregateId, BuildEnvelopeAsync(request, session, cancellationToken), grain.ExecuteAsync);
        await call;
    }

    private async Task<AggregateCommandEnvelope> BuildEnvelopeAsync(TRequest request, SessionContext session, CancellationToken cancellationToken) =>
        new(
            request.GetType().GetQualifiedTypeName(),
            await serializer.SerializeAsync(request, session.TenantId, session.ActorUserId, cancellationToken),
            JsonSerializer.Serialize(session));
}
