using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Stratara.Abstractions.Projections;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Outbox.RabbitMQ.Projections;

/// <summary>
/// Redis-backed implementation of <see cref="IProjectionReplayState"/>. Coordinates the
/// "replay in progress" flag, progress counters, error state, and the replay-request pub/sub
/// channel across all worker instances of a Stratara deployment.
/// </summary>
/// <remarks>
/// <para>
/// Keys are namespaced under <c>stratara:projection:replay:*</c>. The dispatchers in this
/// package and the Orleans execution model consult <see cref="IsReplayActive"/> on every publish,
/// every dispatch and every catch-up to suspend the fast-path while a replay is running; the replay
/// worker uses <see cref="SetProgress"/> / <see cref="SetFailed"/> to surface progress to UI consumers
/// polling <see cref="GetProgress"/>.
/// </para>
/// <para>
/// <see cref="IsReplayActive"/> is answered from a field and never waits on Redis. The field is
/// refreshed asynchronously every <see cref="ProjectionReplayOptions.RefreshSeconds"/>, and at once
/// when a message arrives on the state channel, which <see cref="Activate"/>, <see cref="Deactivate"/>
/// and <see cref="SetFailed"/> publish to. The message is a wake-up, not the truth: a refresh reads the
/// marking itself, so two transitions in quick succession cannot leave the field behind the marking.
/// A transition made through this instance is seen in it at once, and a refresh whose read was in
/// flight when that transition happened is discarded rather than written over it. Until the first
/// refresh has completed the field says that no replay is active; a refresh that fails keeps the
/// field, is logged once per failing stretch, and the next one that succeeds is logged too. A state
/// channel that cannot be subscribed to is tried again on every tick and logged the same way, apart
/// from the refresh: the marking is then seen on the period only.
/// </para>
/// <para>
/// The active marking and the progress counters are held on the lease configured by
/// <see cref="ProjectionReplayOptions.LeaseSeconds"/>, which <see cref="SetProgress"/> renews. A
/// replay whose host stops without reaching <see cref="Deactivate"/> therefore stops renewing it and
/// the marking lapses on its own, rather than suppressing publication for good; the lapse is seen by
/// the refresh. The recorded error is deliberately not leased: it describes a replay that has already
/// ended and is cleared by the next <see cref="Activate"/>.
/// </para>
/// <para>
/// A request travels with its identity, and <see cref="TryActivate"/> claims it in one atomic step with the
/// marking: every host subscribed to the request channel receives it, but only the first to claim it runs it,
/// and a host receiving it after its replay ended finds the claim taken. Claims are kept for a day.
/// <see cref="Complete"/> writes the outcome to a hash that has no lease — it suppresses nothing — and the next
/// completion replaces it.
/// </para>
/// </remarks>
internal sealed class ProjectionReplayState : IProjectionReplayState, IDisposable, IAsyncDisposable
{
    private const string CacheKey = "stratara:projection:replay:active";
    private const string ProcessedKey = "stratara:projection:replay:processed";
    private const string TotalKey = "stratara:projection:replay:total";
    private const string ErrorKey = "stratara:projection:replay:error";
    private const string Channel = "stratara:projection:replay:request";
    private const string StateChannel = "stratara:projection:replay:state";
    private const string RequestIdKey = "stratara:projection:replay:request-id";
    private const string LastKey = "stratara:projection:replay:last";
    private const string ClaimKeyPrefix = "stratara:projection:replay:claimed:";
    private const string StateChanged = "changed";
    private const string Active = "true";
    private const string Yes = "1";
    private const string No = "0";
    private static readonly TimeSpan ClaimRetention = TimeSpan.FromDays(1);

    /// <summary>
    /// Claims a request and, unless another replay holds the marking, marks its replay started — atomically, so two
    /// hosts receiving one request cannot both run it, and a host receiving it after its replay ended finds the claim
    /// taken. Returns 0 for a request claimed before, -1 for a replay already active, 1 for this caller to run it.
    /// The marking keeps the value <c>"true"</c>, which hosts of earlier releases read.
    /// </summary>
    private const string TryActivateScript = """
        if not redis.call('SET', KEYS[1], '1', 'NX', 'EX', ARGV[1]) then return 0 end
        if not redis.call('SET', KEYS[2], 'true', 'NX', 'PX', ARGV[2]) then return -1 end
        redis.call('DEL', KEYS[3], KEYS[5], KEYS[6])
        redis.call('SET', KEYS[4], ARGV[3], 'PX', ARGV[2])
        return 1
        """;

    /// <summary>
    /// Writes the outcome and, while the marking still belongs to the completing request, ends the marking, its
    /// counters and its identity, in one step. A replay that outlived its lease while another request started records
    /// its outcome and leaves the other replay running.
    /// </summary>
    private const string CompleteScript = """
        redis.call('DEL', KEYS[1])
        redis.call('HSET', KEYS[1], 'requestId', ARGV[1], 'startedAt', ARGV[2], 'endedAt', ARGV[3], 'replayed', ARGV[4], 'result', ARGV[5], 'error', ARGV[6], 'restored', ARGV[8])
        local running = redis.call('GET', KEYS[5])
        if running == false or running == ARGV[1] then
            redis.call('DEL', KEYS[2], KEYS[3], KEYS[4], KEYS[5])
        end
        if ARGV[7] == '1' then redis.call('SET', KEYS[6], ARGV[6]) else redis.call('DEL', KEYS[6]) end
        return 1
        """;

    /// <summary>Records the counters and renews the marking's lease, its identity's and the counters' in one round trip.</summary>
    private const string SetProgressScript = """
        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[3])
        redis.call('SET', KEYS[2], ARGV[2], 'PX', ARGV[3])
        redis.call('PEXPIRE', KEYS[3], ARGV[3])
        redis.call('PEXPIRE', KEYS[4], ARGV[3])
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger? _logger;
    private readonly TimeSpan _lease;
    private readonly TimeSpan _refresh;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stoppingToken;
    private readonly TaskCompletionSource _firstRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _loop;

    private volatile bool _active;
    private int _generation;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _startedAt = new();
    private ChannelMessageQueue? _subscription;
    private bool _refreshFailing;
    private bool _subscribeFailing;

    public ProjectionReplayState(
        IConnectionMultiplexer redis,
        IOptions<ProjectionReplayOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<ProjectionReplayState>? logger = null)
    {
        _redis = redis;
        _logger = logger;
        _lease = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
        _refresh = TimeSpan.FromSeconds(options.Value.RefreshSeconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _stoppingToken = _stopping.Token;
        _loop = RunAsync(_stoppingToken);
    }

    /// <inheritdoc/>
    public bool IsReplayActive => _active;

    /// <summary>Completes once the first refresh has run, whether or not it succeeded.</summary>
    internal Task FirstRefresh => _firstRefresh.Task;

    /// <summary>Whether the refresh loop has ended.</summary>
    internal bool IsStopped => _loop.IsCompleted;

    /// <inheritdoc/>
    public void Activate()
    {
        var db = _redis.GetDatabase();
        db.KeyDelete(ErrorKey);
        db.StringSet(CacheKey, Active, _lease);
        db.StringSet(RequestIdKey, Guid.Empty.ToString("N"), _lease);
        _startedAt[Guid.Empty] = _timeProvider.GetUtcNow();
        Transition(active: true);
    }

    /// <inheritdoc/>
    public bool TryActivate(Guid requestId)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var claimed = (int)_redis.GetDatabase().ScriptEvaluate(
            TryActivateScript,
            [ClaimKeyPrefix + requestId.ToString("N"), CacheKey, ErrorKey, RequestIdKey, ProcessedKey, TotalKey],
            [(long)ClaimRetention.TotalSeconds, (long)_lease.TotalMilliseconds, requestId.ToString("N")]);

        if (claimed < 0)
        {
            _logger?.LogProjectionReplayRequestNotRun(requestId);
        }

        if (claimed <= 0)
        {
            return false;
        }

        _startedAt[requestId] = startedAt;
        Transition(active: true);
        return true;
    }

    /// <inheritdoc/>
    public void Complete(ReplayCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var endedAt = _timeProvider.GetUtcNow();
        var startedAt = _startedAt.TryRemove(completion.RequestId, out var started) ? started : endedAt;
        var failed = completion.Result == ReplayResult.Failed;
        _redis.GetDatabase().ScriptEvaluate(
            CompleteScript,
            [LastKey, CacheKey, ProcessedKey, TotalKey, RequestIdKey, ErrorKey],
            [
                completion.RequestId.ToString("N"),
                startedAt.ToUnixTimeMilliseconds(),
                endedAt.ToUnixTimeMilliseconds(),
                completion.ReplayedEvents,
                completion.Result.ToString(),
                completion.ErrorMessage ?? string.Empty,
                failed ? Yes : No,
                No,
            ]);
        Transition(active: false);
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        var db = _redis.GetDatabase();
        db.KeyDelete([CacheKey, ProcessedKey, TotalKey, ErrorKey, RequestIdKey]);
        Transition(active: false);
    }

    /// <inheritdoc/>
    public void SetFailed(string errorMessage)
    {
        var db = _redis.GetDatabase();
        db.KeyDelete([CacheKey, ProcessedKey, TotalKey, RequestIdKey]);
        db.StringSet(ErrorKey, errorMessage);
        Transition(active: false);
    }

    /// <inheritdoc/>
    public void SetProgress(long processedEvents, long totalEvents)
    {
        _redis.GetDatabase().ScriptEvaluate(
            SetProgressScript,
            [ProcessedKey, TotalKey, CacheKey, RequestIdKey],
            [processedEvents, totalEvents, (long)_lease.TotalMilliseconds]);
    }

    /// <inheritdoc/>
    public ReplayProgress GetProgress()
    {
        var db = _redis.GetDatabase();
        var values = db.StringGet([CacheKey, ProcessedKey, TotalKey, ErrorKey, RequestIdKey]);
        var last = db.HashGetAll(LastKey);

        var isActive = values[0] == Active;
        var processed = isActive && values[1].HasValue ? (long)values[1] : 0;
        var total = isActive && values[2].HasValue ? (long)values[2] : 0;
        var percentage = total > 0 ? (int)(processed * 100 / total) : 0;
        var errorMessage = values[3].HasValue ? (string?)values[3] : null;
        Guid? requestId = isActive && Guid.TryParse((string?)values[4], out var running) && running != Guid.Empty ? running : null;

        return new ReplayProgress(isActive, processed, total, percentage, errorMessage)
        {
            RequestId = requestId,
            LastReplay = ReadOutcome(last),
        };
    }

    /// <inheritdoc/>
    public async Task SubscribeToReplayRequestAsync(Func<Task> onReplayRequested, CancellationToken cancellationToken = default)
    {
        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(Channel), async (_, _) =>
        {
            await onReplayRequested();
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A request published by a host of an earlier release carries no identity; each receiving host runs it under one
    /// it draws for itself, so the claim cannot tell the hosts' copies apart — the marking still lets one of them run at
    /// a time, but a host that receives the request after that replay ended runs it again. A host of an earlier
    /// release claims nothing and runs every request it receives, so upgrade every host that runs the replay worker
    /// before relying on one request starting one replay.
    /// </remarks>
    public async Task SubscribeToReplayRequestAsync(Func<Guid, Task> onReplayRequested, CancellationToken cancellationToken = default)
    {
        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(Channel), async (_, message) =>
        {
            var requestId = Guid.TryParse((string?)message, out var parsed) ? parsed : Guid.CreateVersion7();
            await onReplayRequested(requestId);
        });
    }

    /// <inheritdoc/>
    public void RequestReplay() => RequestReplay(Guid.CreateVersion7());

    /// <inheritdoc/>
    public void RequestReplay(Guid requestId)
    {
        var subscriber = _redis.GetSubscriber();
        subscriber.Publish(RedisChannel.Literal(Channel), requestId.ToString("N"));
    }

    /// <summary>Stops the refresh; the loop ends at its next await and the subscription goes with the connection.</summary>
    public void Dispose()
    {
        if (!_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
        }
    }

    /// <summary>Stops the refresh, waits for the loop to end and drops the state subscription.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _loop;
        if (_subscription is { } subscription)
        {
            _subscription = null;
            await subscription.UnsubscribeAsync();
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// Records a transition this instance made: the generation moves first, so a refresh whose read was in flight
    /// finds it changed and discards what it read; then the field, then the announcement to every host sharing the
    /// store, which read the marking themselves.
    /// </summary>
    private void Transition(bool active)
    {
        Interlocked.Increment(ref _generation);
        _active = active;
        _redis.GetSubscriber().Publish(RedisChannel.Literal(StateChannel), StateChanged);
    }

    /// <summary>
    /// One refresh at once, then one per period. A subscription that could not be established is tried again on each
    /// tick, so a connection that was not ready when the host started still gets its announcements.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(_refresh, _timeProvider);
        try
        {
            do
            {
                await EnsureSubscribedAsync();
                await RefreshAsync(cancellationToken);
                _firstRefresh.TrySetResult();
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException stopped) when (cancellationToken.IsCancellationRequested)
        {
            _ = stopped;
        }

        _firstRefresh.TrySetResult();
    }

    private async Task EnsureSubscribedAsync()
    {
        if (_subscription is not null)
        {
            return;
        }

        try
        {
            var queue = await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(StateChannel));
            queue.OnMessage(_ => OnStateChangedAsync());
            _subscription = queue;
            if (_subscribeFailing)
            {
                _subscribeFailing = false;
                _logger?.LogProjectionReplayStateSubscribed();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_subscribeFailing)
            {
                return;
            }

            _subscribeFailing = true;
            _logger?.LogProjectionReplayStateSubscriptionFailed(exception);
        }
    }

    private async Task OnStateChangedAsync()
    {
        try
        {
            await RefreshAsync(_stoppingToken);
        }
        catch (OperationCanceledException stopped) when (_stoppingToken.IsCancellationRequested)
        {
            _ = stopped;
        }
    }

    /// <summary>
    /// Reads the marking and sets the field to it, unless this instance made a transition while the read was in
    /// flight: what was read may predate that transition, and the next refresh reads again. Refreshes are serialised,
    /// so a read that began earlier cannot land after a later one; a refresh that fails keeps the field and is logged
    /// once per failing stretch.
    /// </summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            var generation = Volatile.Read(ref _generation);
            var value = await _redis.GetDatabase().StringGetAsync(CacheKey);
            if (Volatile.Read(ref _generation) == generation)
            {
                _active = value == Active;
            }

            if (_refreshFailing)
            {
                _refreshFailing = false;
                _logger?.LogProjectionReplayRefreshRecovered();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordRefreshFailure(exception);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private static ReplayOutcome? ReadOutcome(HashEntry[] entries)
    {
        if (entries.Length == 0)
        {
            return null;
        }

        var fields = entries.ToDictionary(entry => (string)entry.Name!, entry => (string?)entry.Value);
        if (!Guid.TryParse(fields.GetValueOrDefault("requestId"), out var requestId)
            || !long.TryParse(fields.GetValueOrDefault("startedAt"), out var startedAt)
            || !long.TryParse(fields.GetValueOrDefault("endedAt"), out var endedAt)
            || !long.TryParse(fields.GetValueOrDefault("replayed"), out var replayed)
            || !Enum.TryParse<ReplayResult>(fields.GetValueOrDefault("result"), out var result))
        {
            return null;
        }

        var error = fields.GetValueOrDefault("error");
        return new ReplayOutcome(
            requestId,
            DateTimeOffset.FromUnixTimeMilliseconds(startedAt),
            DateTimeOffset.FromUnixTimeMilliseconds(endedAt),
            replayed,
            result,
            string.IsNullOrEmpty(error) ? null : error);
    }

    private void RecordRefreshFailure(Exception exception)
    {
        if (_refreshFailing)
        {
            return;
        }

        _refreshFailing = true;
        _logger?.LogProjectionReplayRefreshFailed(exception);
    }
}
