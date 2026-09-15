using JetBrains.Annotations;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.Sagas;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Sagas;

public sealed record ProcessStarted(Guid CounterId, DateTimeOffset ExpiresAt);

public sealed record ProcessExpired(Guid CounterId);

/// <summary>The process's state: an aggregate of its own, folded from its own stream.</summary>
public sealed class TimeoutProcessState : ISagaProcessState
{
    public Guid Id { get; set; }

    public bool Started { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public bool Expired { get; set; }

    public bool Completed => Expired;

    [UsedImplicitly]
    public void Apply(ProcessStarted @event)
    {
        Id = @event.CounterId;
        Started = true;
        ExpiresAt = @event.ExpiresAt;
    }

    [UsedImplicitly]
    public void Apply(ProcessExpired @event) => Expired = true;
}

/// <summary>
/// A process that starts when a counter is created and expires after a fixed time unless the
/// process is over. The timeout is what a hard kill must not lose.
/// </summary>
public sealed class TimeoutSaga(TimeProvider timeProvider) : SagaProcess<TimeoutProcessState>
{
    public const string ExpirePurpose = "expire";
    public static readonly TimeSpan ExpiresAfter = TimeSpan.FromSeconds(3);

    public override bool Handles(IEvent @event) => @event.Data is CounterCreated;

    public override Guid CorrelationOf(IEvent @event) => ((CounterCreated)@event.Data).CounterId;

    public override Task HandleAsync(TimeoutProcessState state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken)
    {
        if (state.Started)
        {
            return Task.CompletedTask;
        }

        var expiresAt = timeProvider.GetUtcNow() + ExpiresAfter;
        context.Emit(new ProcessStarted(((CounterCreated)@event.Data).CounterId, expiresAt));
        context.Schedule(ExpirePurpose, expiresAt);
        return Task.CompletedTask;
    }

    public override Task OnTimeoutAsync(TimeoutProcessState state, string purpose, ISagaProcessContext context, CancellationToken cancellationToken)
    {
        if (purpose == ExpirePurpose && !state.Expired)
        {
            context.Emit(new ProcessExpired(state.Id));
        }

        return Task.CompletedTask;
    }
}
