using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Stratara.Abstractions.Projections;

namespace Stratara.Outbox.RabbitMQ.Projections;

/// <summary>
/// Redis-backed implementation of <see cref="IProjectionReplayState"/>. Coordinates the
/// "replay in progress" flag, progress counters, error state, and the replay-request pub/sub
/// channel across all worker instances of a Stratara deployment.
/// </summary>
/// <remarks>
/// <para>
/// Keys are namespaced under <c>stratara:projection:replay:*</c>. The dispatchers in this
/// package consult <see cref="IsReplayActive"/> on every publish to suspend the fast-path while
/// a replay is running; the replay worker uses <see cref="SetProgress"/> / <see cref="SetFailed"/>
/// to surface progress to UI consumers polling <see cref="GetProgress"/>.
/// </para>
/// <para>
/// The active marking and the progress counters are held on the lease configured by
/// <see cref="ProjectionReplayOptions.LeaseSeconds"/>, which <see cref="SetProgress"/> renews. A
/// replay whose host stops without reaching <see cref="Deactivate"/> therefore stops renewing it and
/// the marking lapses on its own, rather than suppressing publication for good. The recorded error
/// is deliberately not leased: it describes a replay that has already ended and is cleared by the
/// next <see cref="Activate"/>.
/// </para>
/// </remarks>
internal sealed class ProjectionReplayState(IConnectionMultiplexer redis, IOptions<ProjectionReplayOptions> options)
    : IProjectionReplayState
{
    private const string CacheKey = "stratara:projection:replay:active";
    private const string ProcessedKey = "stratara:projection:replay:processed";
    private const string TotalKey = "stratara:projection:replay:total";
    private const string ErrorKey = "stratara:projection:replay:error";
    private const string Channel = "stratara:projection:replay:request";

    private readonly TimeSpan _lease = TimeSpan.FromSeconds(options.Value.LeaseSeconds);

    /// <inheritdoc/>
    public bool IsReplayActive
    {
        get
        {
            var db = redis.GetDatabase();
            return db.StringGet(CacheKey) == "true";
        }
    }

    /// <inheritdoc/>
    public void Activate()
    {
        var db = redis.GetDatabase();
        db.KeyDelete(ErrorKey);
        db.StringSet(CacheKey, "true", _lease);
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        var db = redis.GetDatabase();
        db.KeyDelete([CacheKey, ProcessedKey, TotalKey, ErrorKey]);
    }

    /// <inheritdoc/>
    public void SetFailed(string errorMessage)
    {
        var db = redis.GetDatabase();
        db.KeyDelete(CacheKey);
        db.StringSet(ErrorKey, errorMessage);
    }

    /// <inheritdoc/>
    public void SetProgress(long processedEvents, long totalEvents)
    {
        var db = redis.GetDatabase();
        db.StringSet(ProcessedKey, processedEvents, _lease);
        db.StringSet(TotalKey, totalEvents, _lease);
        db.KeyExpire(CacheKey, _lease);
    }

    /// <inheritdoc/>
    public ReplayProgress GetProgress()
    {
        var db = redis.GetDatabase();
        var values = db.StringGet([CacheKey, ProcessedKey, TotalKey, ErrorKey]);

        var isActive = values[0] == "true";
        var processed = values[1].HasValue ? (long)values[1] : 0;
        var total = values[2].HasValue ? (long)values[2] : 0;
        var percentage = total > 0 ? (int)(processed * 100 / total) : 0;
        var errorMessage = values[3].HasValue ? (string?)values[3] : null;

        return new ReplayProgress(isActive, processed, total, percentage, errorMessage);
    }

    /// <inheritdoc/>
    public async Task SubscribeToReplayRequestAsync(Func<Task> onReplayRequested, CancellationToken cancellationToken = default)
    {
        var subscriber = redis.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(Channel), async (_, _) =>
        {
            await onReplayRequested();
        });
    }

    /// <inheritdoc/>
    public void RequestReplay()
    {
        var subscriber = redis.GetSubscriber();
        subscriber.Publish(RedisChannel.Literal(Channel), "replay");
    }
}
