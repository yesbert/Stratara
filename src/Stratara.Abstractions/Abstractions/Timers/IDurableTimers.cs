namespace Stratara.Abstractions.Timers;

/// <summary>
/// Durable, owner-checked, one-shot timers. A timer belongs to an owner — an aggregate, a process,
/// a unit of work — and fires once at its due time, in one place per cluster, surviving restarts. When
/// it fires, the owner is checked first: a timer whose owner is gone unregisters itself and does
/// nothing. The operation that ends an owner cancels its timers in the same call.
/// </summary>
/// <remarks>
/// The timers are stored in the cluster's reminder table and nowhere else. The due time is part of
/// the timer's identity, so registering the same owner and purpose again replaces the earlier timer.
/// Delivery is at least once: a handler that throws sees the timer again after
/// the retry period the host configured, and a handler must tolerate that.
/// </remarks>
public interface IDurableTimers
{
    /// <summary>Registers a timer, replacing any timer with the same owner and purpose.</summary>
    /// <param name="registration">The owner, purpose and due time.</param>
    /// <param name="cancellationToken">Propagated to the registration.</param>
    /// <returns>A task that completes when the timer is registered.</returns>
    /// <exception cref="ArgumentException">
    /// The purpose is empty, contains <c>@</c>, or is longer than the timer store holds (130 characters); or the owner id
    /// is empty or longer than the timer store holds (139 characters).
    /// </exception>
    Task RegisterAsync(TimerRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>Cancels one timer of an owner. Cancelling a timer that does not exist is not an error.</summary>
    /// <param name="ownerId">The owner.</param>
    /// <param name="purpose">The purpose the timer was registered with.</param>
    /// <param name="cancellationToken">Propagated to the cancellation.</param>
    /// <exception cref="ArgumentException">The owner id is empty or longer than the timer store holds (139 characters).</exception>
    Task CancelAsync(string ownerId, string purpose, CancellationToken cancellationToken = default);

    /// <summary>Cancels every timer of an owner — the call the operation that ends the owner makes.</summary>
    /// <param name="ownerId">The owner.</param>
    /// <param name="cancellationToken">Propagated to the cancellation.</param>
    /// <exception cref="ArgumentException">The owner id is empty or longer than the timer store holds (139 characters).</exception>
    Task CancelAllAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Lists the timers an owner currently has, for diagnostics and tests.</summary>
    /// <param name="ownerId">The owner.</param>
    /// <param name="cancellationToken">Propagated to the lookup.</param>
    /// <returns>The registrations, or an empty list.</returns>
    /// <exception cref="ArgumentException">The owner id is empty or longer than the timer store holds (139 characters).</exception>
    Task<IReadOnlyList<TimerRegistration>> ListAsync(string ownerId, CancellationToken cancellationToken = default);
}

/// <summary>A timer to register: who it belongs to, what it is for, and when it is due.</summary>
/// <param name="OwnerId">The owner the timer is checked against when it fires.</param>
/// <param name="Purpose">What the timer is for; unique per owner. Must not contain <c>@</c>.</param>
/// <param name="DueAt">When the timer fires.</param>
public sealed record TimerRegistration(string OwnerId, string Purpose, DateTimeOffset DueAt);

/// <summary>A timer that has fired: the registration and the moment it fired.</summary>
/// <param name="OwnerId">The owner, confirmed to exist at the time of firing.</param>
/// <param name="Purpose">What the timer is for.</param>
/// <param name="DueAt">When it was due.</param>
/// <param name="FiredAt">When it fired; never before <paramref name="DueAt"/>.</param>
public sealed record TimerDue(string OwnerId, string Purpose, DateTimeOffset DueAt, DateTimeOffset FiredAt);

/// <summary>
/// Answers whether a timer's owner still exists. Implemented by the host: an aggregate that has not
/// been closed, a process that is still running, an intent that has not been completed.
/// </summary>
public interface ITimerOwners
{
    /// <summary>Returns whether <paramref name="ownerId"/> still exists.</summary>
    /// <param name="ownerId">The owner a due timer belongs to.</param>
    /// <param name="cancellationToken">Propagated to the lookup.</param>
    Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken);
}

/// <summary>
/// Receives timers that are due and whose owner exists. Implemented by the host. Resolved from a
/// fresh scope per firing, so it may take scoped dependencies.
/// </summary>
public interface ITimerHandler
{
    /// <summary>Handles a due timer. Throwing keeps the timer registered for a retry.</summary>
    /// <param name="due">The timer that fired.</param>
    /// <param name="cancellationToken">
    /// Requested when the process running the handler stops and the handler has not completed within the time the host
    /// gives a stop. A handler that observes it stops; the timer stays registered and fires again elsewhere, as after a
    /// failure. A handler that ignores it runs to its end.
    /// </param>
    Task OnDueAsync(TimerDue due, CancellationToken cancellationToken);
}
