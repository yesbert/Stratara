using Stratara.Abstractions.EventSourcing;

namespace Stratara.Sagas.Abstractions;

/// <summary>
/// A saga with state, correlation and timeouts — the additional interface the stateless <c>ISaga</c>
/// contract is left untouched for. The state is an aggregate of the process's own: it lives in an
/// event stream keyed by the process and its correlation, is rebuilt by folding that stream, and
/// changes only through the events the process emits. A timeout is a durable, owner-checked timer
/// whose owner is the process; when the process completes, its timers go with it.
/// </summary>
/// <remarks>
/// <para>
/// A fact may reach a process more than once — a host that dies after handling a fact and before
/// recording that it read it hands the fact over again. Every delivery receives the state as
/// recorded, which already reflects the first handling if that handling's events were recorded, so a
/// process decides from its state and not from having seen the fact.
/// </para>
/// <para>
/// A step that schedules or cancels a timeout also emits the event that records why. A step's timer
/// changes are applied before its events are recorded, so a timeout may reach
/// <see cref="OnTimeoutAsync(TState, string, ISagaProcessContext, CancellationToken)"/> for a step
/// whose events never were; the timeout receives the state as recorded, and a timeout that state does
/// not expect does nothing.
/// </para>
/// </remarks>
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
    /// <param name="event">The fact, in commit order; possibly delivered before.</param>
    /// <param name="context">Where emitted events and timeouts go.</param>
    /// <param name="cancellationToken">Propagated to the handling.</param>
    /// <returns>A task that completes when the step has been decided.</returns>
    public abstract Task HandleAsync(TState state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken);

    /// <summary>A timeout the process scheduled is due and the process has not completed.</summary>
    /// <param name="state">The process state, folded from its stream; decide from it whether the timeout is still expected.</param>
    /// <param name="purpose">The purpose the timeout was scheduled with.</param>
    /// <param name="context">Where emitted events and timeouts go.</param>
    /// <param name="cancellationToken">Propagated to the handling.</param>
    /// <returns>A task that completes when the step has been decided.</returns>
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
    /// <returns><see langword="true"/> when the process handles the fact.</returns>
    bool Handles(IEvent @event);

    /// <summary>The correlation the fact belongs to; one process instance per correlation.</summary>
    /// <param name="event">A fact the process handles.</param>
    /// <returns>The correlation id of the process instance the fact advances.</returns>
    Guid CorrelationOf(IEvent @event);

    /// <summary>Advances the process.</summary>
    /// <param name="state">The folded state.</param>
    /// <param name="event">The fact.</param>
    /// <param name="context">Where emitted events and timeouts go.</param>
    /// <param name="cancellationToken">Propagated to the handling.</param>
    /// <returns>A task that completes when the step has been decided.</returns>
    Task HandleAsync(object state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken);

    /// <summary>A scheduled timeout is due.</summary>
    /// <param name="state">The folded state.</param>
    /// <param name="purpose">The purpose the timeout was scheduled with.</param>
    /// <param name="context">Where emitted events and timeouts go.</param>
    /// <param name="cancellationToken">Propagated to the handling.</param>
    /// <returns>A task that completes when the step has been decided.</returns>
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

    /// <summary>Schedules a timeout, replacing one with the same purpose. Emit the event that records why in the same step.</summary>
    /// <param name="purpose">What the timeout is for.</param>
    /// <param name="dueAt">When it fires.</param>
    void Schedule(string purpose, DateTimeOffset dueAt);

    /// <summary>Cancels a timeout; cancelling one that does not exist is not an error.</summary>
    /// <param name="purpose">What the timeout was for.</param>
    void Cancel(string purpose);
}
