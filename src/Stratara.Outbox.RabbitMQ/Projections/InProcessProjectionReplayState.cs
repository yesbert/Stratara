using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Projections;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Outbox.RabbitMQ.Projections;

/// <summary>
/// In-process implementation of <see cref="IProjectionReplayState"/>, used where no shared
/// coordination store is registered. The active marking, the progress counters, the failure message
/// and the replay-request subscribers all live in this process, so a replay requested here is seen
/// here only; the registration that chooses this implementation records that once at start-up.
/// </summary>
/// <remarks>
/// The lease semantics mirror the Redis-backed implementation: <see cref="TryActivate"/>, <see cref="Activate"/> and
/// <see cref="SetProgress"/> stamp an expiry <see cref="ProjectionReplayOptions.LeaseSeconds"/>
/// ahead, and an expired marking reads as inactive with its counters cleared. The recorded error is
/// not leased; it describes a replay that has ended and is cleared when the next replay is activated or completes
/// without failing. A completion whose replay outlived its lease while another started records its outcome and leaves
/// the other replay's marking and error alone.
/// A subscriber that fails when a replay is requested is logged and does not stop the remaining
/// subscribers from being notified, as a pub/sub handler's failure never reaches the publisher.
/// A request is claimed once: the identities of the most recent requests are remembered, so a request
/// delivered twice runs once. The outcome of the last replay that ended is kept until the next one ends.
/// </remarks>
internal sealed class InProcessProjectionReplayState(
    IOptions<ProjectionReplayOptions> options,
    TimeProvider timeProvider,
    ILogger<InProcessProjectionReplayState>? logger = null)
    : IProjectionReplayState
{
    private const int RememberedRequests = 1024;

    private readonly TimeSpan _lease = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
    private readonly object _gate = new();
    private readonly List<Func<Guid, Task>> _subscribers = [];
    private readonly HashSet<Guid> _claimed = [];
    private readonly Queue<Guid> _claimOrder = new();

    private DateTimeOffset? _activeUntil;
    private long _processed;
    private long _total;
    private string? _error;
    private readonly Dictionary<Guid, DateTimeOffset> _startedAt = [];
    private Guid? _requestId;
    private ReplayOutcome? _lastReplay;

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
            _requestId = Guid.Empty;
            var startedAt = timeProvider.GetUtcNow();
            _startedAt[Guid.Empty] = startedAt;
            _activeUntil = startedAt + _lease;
        }
    }

    /// <inheritdoc/>
    public bool TryActivate(Guid requestId)
    {
        lock (_gate)
        {
            if (!Claim(requestId))
            {
                return false;
            }

            if (IsLeaseAlive())
            {
                logger?.LogProjectionReplayRequestNotRun(requestId);
                return false;
            }

            _error = null;
            _processed = 0;
            _total = 0;
            _requestId = requestId;
            var startedAt = timeProvider.GetUtcNow();
            _startedAt[requestId] = startedAt;
            _activeUntil = startedAt + _lease;
            return true;
        }
    }

    /// <inheritdoc/>
    public void Complete(ReplayCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_gate)
        {
            var endedAt = timeProvider.GetUtcNow();
            var startedAt = _startedAt.Remove(completion.RequestId, out var started) ? started : endedAt;
            _lastReplay = new ReplayOutcome(
                completion.RequestId,
                startedAt,
                endedAt,
                completion.ReplayedEvents,
                completion.Result,
                completion.ErrorMessage)
            {
                ReadModelsRestored = completion.ReadModelsRestored,
            };
            if (_requestId is null || _requestId == completion.RequestId)
            {
                _error = completion.Result == ReplayResult.Failed ? completion.ErrorMessage : null;
                EndReplay();
            }
        }
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        lock (_gate)
        {
            _error = null;
            _startedAt.Clear();
            EndReplay();
        }
    }

    /// <inheritdoc/>
    public void SetFailed(string errorMessage)
    {
        lock (_gate)
        {
            _error = errorMessage;
            _startedAt.Clear();
            EndReplay();
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
            return new ReplayProgress(isActive, processed, total, percentage, _error)
            {
                RequestId = isActive && _requestId != Guid.Empty ? _requestId : null,
                LastReplay = _lastReplay,
            };
        }
    }

    /// <inheritdoc/>
    public Task SubscribeToReplayRequestAsync(Func<Task> onReplayRequested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onReplayRequested);
        return SubscribeToReplayRequestAsync(_ => onReplayRequested(), cancellationToken);
    }

    /// <inheritdoc/>
    public Task SubscribeToReplayRequestAsync(Func<Guid, Task> onReplayRequested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onReplayRequested);
        lock (_gate)
        {
            _subscribers.Add(onReplayRequested);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void RequestReplay() => RequestReplay(Guid.CreateVersion7());

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="requestId"/> is <see cref="Guid.Empty"/>.</exception>
    public void RequestReplay(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A replay request needs an identity other than Guid.Empty.", nameof(requestId));
        }

        Func<Guid, Task>[] subscribers;
        lock (_gate)
        {
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            _ = NotifyAsync(subscriber, requestId);
        }
    }

    private async Task NotifyAsync(Func<Guid, Task> subscriber, Guid requestId)
    {
        try
        {
            await subscriber(requestId);
        }
        catch (Exception exception)
        {
            // A subscriber's failure never reaches the publisher, as with a pub/sub handler; it is
            // logged where a logger exists and swallowed otherwise so the remaining subscribers run.
            logger?.LogProjectionReplayRequestSubscriberFailed(exception);
        }
    }

    private bool Claim(Guid requestId)
    {
        if (!_claimed.Add(requestId))
        {
            return false;
        }

        _claimOrder.Enqueue(requestId);
        if (_claimOrder.Count > RememberedRequests)
        {
            _claimed.Remove(_claimOrder.Dequeue());
        }

        return true;
    }

    private void EndReplay()
    {
        _activeUntil = null;
        _processed = 0;
        _total = 0;
        _requestId = null;
    }

    private bool IsLeaseAlive() => _activeUntil is { } until && until > timeProvider.GetUtcNow();
}
