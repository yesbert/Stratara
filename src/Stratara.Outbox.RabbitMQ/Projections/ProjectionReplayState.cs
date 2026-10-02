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
/// A transition made through this instance is seen in it at once. Until the first refresh has
/// completed the field says that no replay is active; a refresh that fails keeps the field, is logged
/// once per failing stretch, and the next one that succeeds is logged too.
/// </para>
/// <para>
/// The active marking and the progress counters are held on the lease configured by
/// <see cref="ProjectionReplayOptions.LeaseSeconds"/>, which <see cref="SetProgress"/> renews. A
/// replay whose host stops without reaching <see cref="Deactivate"/> therefore stops renewing it and
/// the marking lapses on its own, rather than suppressing publication for good; the lapse is seen by
/// the refresh. The recorded error is deliberately not leased: it describes a replay that has already
/// ended and is cleared by the next <see cref="Activate"/>.
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
    private const string StateChanged = "changed";
    private const string Active = "true";

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
    private ChannelMessageQueue? _subscription;
    private bool _refreshFailing;

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
        _active = true;
        Announce();
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        var db = _redis.GetDatabase();
        db.KeyDelete([CacheKey, ProcessedKey, TotalKey, ErrorKey]);
        _active = false;
        Announce();
    }

    /// <inheritdoc/>
    public void SetFailed(string errorMessage)
    {
        var db = _redis.GetDatabase();
        db.KeyDelete(CacheKey);
        db.StringSet(ErrorKey, errorMessage);
        _active = false;
        Announce();
    }

    /// <inheritdoc/>
    public void SetProgress(long processedEvents, long totalEvents)
    {
        var db = _redis.GetDatabase();
        db.StringSet(ProcessedKey, processedEvents, _lease);
        db.StringSet(TotalKey, totalEvents, _lease);
        db.KeyExpire(CacheKey, _lease);
    }

    /// <inheritdoc/>
    public ReplayProgress GetProgress()
    {
        var db = _redis.GetDatabase();
        var values = db.StringGet([CacheKey, ProcessedKey, TotalKey, ErrorKey]);

        var isActive = values[0] == Active;
        var processed = values[1].HasValue ? (long)values[1] : 0;
        var total = values[2].HasValue ? (long)values[2] : 0;
        var percentage = total > 0 ? (int)(processed * 100 / total) : 0;
        var errorMessage = values[3].HasValue ? (string?)values[3] : null;

        return new ReplayProgress(isActive, processed, total, percentage, errorMessage);
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
    public void RequestReplay()
    {
        var subscriber = _redis.GetSubscriber();
        subscriber.Publish(RedisChannel.Literal(Channel), "replay");
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

    /// <summary>Tells every host sharing the store that the marking changed; the hosts read it themselves.</summary>
    private void Announce() => _redis.GetSubscriber().Publish(RedisChannel.Literal(StateChannel), StateChanged);

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
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordRefreshFailure(exception);
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
    /// Reads the marking and sets the field to it. Refreshes are serialised, so a read that began earlier cannot land
    /// after a later one; a refresh that fails keeps the field and is logged once per failing stretch.
    /// </summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(CacheKey);
            _active = value == Active;
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
