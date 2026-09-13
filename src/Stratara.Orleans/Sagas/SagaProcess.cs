using Stratara.Abstractions.EventSourcing;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Sagas;

/// <summary>
/// A saga with state, correlation and timeouts — the additional interface the stateless <c>ISaga</c>
/// contract is left untouched for. The state is an aggregate of the process's own: it lives in an
/// event stream keyed by the correlation id, is rebuilt by folding that stream, and changes only
/// through the events the process emits. Nothing is kept in grain storage. A timeout is a durable,
/// owner-checked timer whose owner is the process; when the process completes, its timers go with it.
/// </summary>
/// <typeparam name="TState">The process state: an aggregate with <c>Apply</c> methods for the events the process emits.</typeparam>
public abstract class SagaProcess<TState> : ISagaProcess where TState : class, ISagaProcessState, new()
{
    /// <inheritdoc/>
    public Type StateType => typeof(TState);

    /// <inheritdoc/>
    public abstract bool Handles(IEvent @event);

    /// <inheritdoc/>
    public abstract Guid CorrelationOf(IEvent @event);

    /// <summary>Advances the process with a fact. Emit state events and schedule or cancel timeouts through the context.</summary>
    /// <param name="state">The process state, folded from its stream; fresh for a process that has not started.</param>
    /// <param name="event">The fact, in commit order.</param>
    /// <param name="context">Where emitted events and timeouts go.</param>
    /// <param name="cancellationToken">Propagated to the handling.</param>
    public abstract Task HandleAsync(TState state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken);

    /// <summary>A timeout the process scheduled is due and the process has not completed.</summary>
    /// <param name="state">The process state, folded from its stream.</param>
    /// <param name="purpose">The purpose the timeout was scheduled with.</param>
    /// <param name="context">Where emitted events and timeouts go.</param>
    /// <param name="cancellationToken">Propagated to the handling.</param>
    public abstract Task OnTimeoutAsync(TState state, string purpose, ISagaProcessContext context, CancellationToken cancellationToken);

    Task ISagaProcess.HandleAsync(object state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken) =>
        HandleAsync((TState)state, @event, context, cancellationToken);

    Task ISagaProcess.OnTimeoutAsync(object state, string purpose, ISagaProcessContext context, CancellationToken cancellationToken) =>
        OnTimeoutAsync((TState)state, purpose, context, cancellationToken);
}

/// <summary>The untyped face of <see cref="SagaProcess{TState}"/> the runtime works with.</summary>
public interface ISagaProcess : ISaga
{
    /// <summary>The state type, an aggregate of the process's own.</summary>
    Type StateType { get; }

    /// <summary>Whether the process starts or advances on this fact.</summary>
    /// <param name="event">A fact from the store.</param>
    bool Handles(IEvent @event);

    /// <summary>The correlation the fact belongs to; one process instance per correlation.</summary>
    /// <param name="event">A fact the process handles.</param>
    Guid CorrelationOf(IEvent @event);

    /// <summary>Advances the process.</summary>
    /// <param name="state">The folded state.</param>
    /// <param name="event">The fact.</param>
    /// <param name="context">Where emitted events and timeouts go.</param>
    /// <param name="cancellationToken">Propagated to the handling.</param>
    Task HandleAsync(object state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken);

    /// <summary>A scheduled timeout is due.</summary>
    /// <param name="state">The folded state.</param>
    /// <param name="purpose">The purpose the timeout was scheduled with.</param>
    /// <param name="context">Where emitted events and timeouts go.</param>
    /// <param name="cancellationToken">Propagated to the handling.</param>
    Task OnTimeoutAsync(object state, string purpose, ISagaProcessContext context, CancellationToken cancellationToken);
}

/// <summary>What a process state must say about itself: whether the process is over.</summary>
public interface ISagaProcessState
{
    /// <summary>Once <see langword="true"/>, the process's timers are cancelled and no timeout reaches it.</summary>
    bool Completed { get; }
}

/// <summary>Collects what a process step produces: state events to append and timeouts to schedule or cancel.</summary>
public interface ISagaProcessContext
{
    /// <summary>Appends an event to the process's stream once the step completes.</summary>
    /// <param name="stateEvent">The event; the state must have an <c>Apply</c> method for it.</param>
    void Emit(object stateEvent);

    /// <summary>Schedules a timeout, replacing one with the same purpose.</summary>
    /// <param name="purpose">What the timeout is for.</param>
    /// <param name="dueAt">When it fires.</param>
    void Schedule(string purpose, DateTimeOffset dueAt);

    /// <summary>Cancels a timeout; cancelling one that does not exist is not an error.</summary>
    /// <param name="purpose">What the timeout was for.</param>
    void Cancel(string purpose);
}
