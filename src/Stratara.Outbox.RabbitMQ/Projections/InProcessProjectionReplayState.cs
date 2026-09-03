using Microsoft.Extensions.Options;
using Stratara.Abstractions.Projections;

namespace Stratara.Outbox.RabbitMQ.Projections;

/// <summary>
/// In-process implementation of <see cref="IProjectionReplayState"/>, used where no shared
/// coordination store is registered. The active marking, the progress counters, the failure message
/// and the replay-request subscribers all live in this process, so a replay requested here is seen
/// here only; the registration that chooses this implementation records that once at start-up.
/// </summary>
/// <remarks>
/// The lease semantics mirror the Redis-backed implementation: <see cref="Activate"/> and
/// <see cref="SetProgress"/> stamp an expiry <see cref="ProjectionReplayOptions.LeaseSeconds"/>
/// ahead, and an expired marking reads as inactive with its counters cleared. The recorded error is
/// not leased; it describes a replay that has ended and is cleared by the next <see cref="Activate"/>.
/// </remarks>
internal sealed class InProcessProjectionReplayState(IOptions<ProjectionReplayOptions> options, TimeProvider timeProvider)
    : IProjectionReplayState
{
    private readonly TimeSpan _lease = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
    private readonly object _gate = new();
    private readonly List<Func<Task>> _subscribers = [];

    private DateTimeOffset? _activeUntil;
    private long _processed;
    private long _total;
    private string? _error;

    /// <inheritdoc/>
    public bool IsReplayActive
    {
        get
        {
            lock (_gate)
            {
                return IsLeaseAlive();
            }
        }
    }

    /// <inheritdoc/>
    public void Activate()
    {
        lock (_gate)
        {
            _error = null;
            _processed = 0;
            _total = 0;
            _activeUntil = timeProvider.GetUtcNow() + _lease;
        }
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        lock (_gate)
        {
            _activeUntil = null;
            _processed = 0;
            _total = 0;
            _error = null;
        }
    }

    /// <inheritdoc/>
    public void SetFailed(string errorMessage)
    {
        lock (_gate)
        {
            _activeUntil = null;
            _error = errorMessage;
        }
    }

    /// <inheritdoc/>
    public void SetProgress(long processedEvents, long totalEvents)
    {
        lock (_gate)
        {
            _processed = processedEvents;
            _total = totalEvents;
            _activeUntil = timeProvider.GetUtcNow() + _lease;
        }
    }

    /// <inheritdoc/>
    public ReplayProgress GetProgress()
    {
        lock (_gate)
        {
            var isActive = IsLeaseAlive();
            var processed = isActive ? _processed : 0;
            var total = isActive ? _total : 0;
            var percentage = total > 0 ? (int)(processed * 100 / total) : 0;
            return new ReplayProgress(isActive, processed, total, percentage, _error);
        }
    }

    /// <inheritdoc/>
    public Task SubscribeToReplayRequestAsync(Func<Task> onReplayRequested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onReplayRequested);
        lock (_gate)
        {
            _subscribers.Add(onReplayRequested);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void RequestReplay()
    {
        Func<Task>[] subscribers;
        lock (_gate)
        {
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            _ = subscriber();
        }
    }

    private bool IsLeaseAlive() => _activeUntil is { } until && until > timeProvider.GetUtcNow();
}
