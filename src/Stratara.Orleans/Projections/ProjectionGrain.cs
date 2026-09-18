using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Polly.Registry;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.CommitOrder;
using Stratara.Projections.Abstractions;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The dispatcher the grains replace, kept for the hybrid shape. A wrapper rather than the
/// interface itself, so that resolving it never resolves the replacement.
/// </summary>
/// <param name="Dispatcher">The bus-backed dispatcher that was registered before the grains.</param>
internal sealed record InnerBundleDispatcher(Stratara.Abstractions.Outbox.IEventBundleOutboxDispatcher Dispatcher);

/// <summary>Settings for the projection grains.</summary>
public sealed class ProjectionGrainOptions
{
    /// <summary>How many entries one read from the store returns.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>The safety net: how often a grain reads the store without having been nudged.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the cluster brings a lost grain back; not shorter than the minimum reminder period.</summary>
    public TimeSpan KeepAlivePeriod { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>One grain per projection and partition, keyed <c>projection/partition</c>.</summary>
[Alias("Stratara.Orleans.IProjectionGrain")]
internal interface IProjectionGrain : IGrainWithStringKey
{
    [Alias("EnsureRunningAsync")]
    Task EnsureRunningAsync();

    /// <summary>
    /// A commit happened in this partition; read now rather than at the next poll. Interleaves with a
    /// running catch-up, which then reads once more before it ends, and costs the caller no reply.
    /// </summary>
    [OneWay]
    [AlwaysInterleave]
    [Alias("NudgeAsync")]
    Task NudgeAsync();

    /// <summary>Reads and applies until the store has nothing newer; returns how many entries were applied.</summary>
    [Alias("CatchUpAsync")]
    Task<int> CatchUpAsync();

    [Alias("PositionAsync")]
    Task<long> PositionAsync();

    /// <summary>
    /// Stops reading until <see cref="ResumeAsync"/>; returns once no batch is in flight. Interleaves with a running
    /// catch-up, which stops at its next batch boundary, so a pause does not wait for a partition far behind.
    /// </summary>
    [AlwaysInterleave]
    [Alias("PauseAsync")]
    Task PauseAsync();

    /// <summary>Reads again, from whatever the checkpoint now says; returns once the read is requested, not once it is done.</summary>
    [Alias("ResumeAsync")]
    Task ResumeAsync();
}

/// <summary>
/// Reads its partition of the event store in commit order from its checkpoint and applies each
/// entry to its projection through the framework's projection handler, under the session recorded
/// with the entry. The checkpoint moves only past entries that applied; an entry that fails — a
/// missing prerequisite past the retry policy, or any other failure — stops the batch where it is,
/// and the next nudge or poll tries again from there.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
[ProjectionsRolePlacementFilter]
internal sealed class ProjectionGrain(
    IServiceScopeFactory scopeFactory,
    IEventMapperFactory eventMapperFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<ProjectionGrainOptions> options,
    IOptions<CommitOrderOptions> commitOrder,
    Microsoft.Extensions.Logging.ILogger<ProjectionGrain> logger)
    : StoreReaderGrain(scopeFactory, replayState, pipelineProvider, new StoreReaderSettings(options.Value.BatchSize, options.Value.PollInterval, options.Value.KeepAlivePeriod, commitOrder.Value.PartitionCount), logger),
        IProjectionGrain
{
    private HashSet<string>? _relevant;
    private Type? _projectionType;

    /// <summary>
    /// One scope, one projection instance and one handler per session run — the consecutive entries recorded under one
    /// session, what one bundle was on the bus — resolved after the run's session is set, so a dependency that takes
    /// its tenant when it is constructed takes the entry's; per entry, the recorded session and the retry policy, as
    /// the <c>projections</c> guarantees require. One relevant-event set for the grain.
    /// </summary>
    protected override async Task<int> ApplyBatchAsync(CommittedBatch batch, CancellationToken cancellationToken)
    {
        await using var runs = new SessionRuns<(IProjectionHandler Handler, IProjection Projection)>(ScopeFactory, services =>
        {
            var handler = services.GetRequiredService<IProjectionHandler>();
            return (handler, ResolveProjection(services, handler));
        });

        return await Loop.ApplyEachAsync(batch, async (entry, entryToken) =>
        {
            var (handler, projection) = await runs.EnterAsync(entry);
            _relevant ??= new HashSet<string>(handler.GetRelevantEventTypeNames(projection), StringComparer.Ordinal);

            var events = await eventMapperFactory.MapToEventsAsync([entry], entryToken);
            var relevantEvents = events.Where(e => _relevant.Contains(e.EventTypeName)).ToList();
            if (relevantEvents.Count == 0)
            {
                return;
            }

            await handler.ProjectAsync(projection, relevantEvents, entryToken);
        }, cancellationToken);
    }

    /// <summary>
    /// The first batch finds the projection among every registered one and remembers its type; every
    /// later batch resolves that type where it is registered as itself, and finds it among the registered
    /// projections again where it is not. The container always builds it, so a factory registration or a
    /// lifetime the host chose holds for every batch.
    /// </summary>
    /// <exception cref="InvalidOperationException">No projection of the grain's name is registered on this silo.</exception>
    private IProjection ResolveProjection(IServiceProvider services, IProjectionHandler handler)
    {
        if (_projectionType is { } type)
        {
            return services.GetService(type) as IProjection
                   ?? services.GetServices<IProjection>().FirstOrDefault(p => p.GetType() == type)
                   ?? throw new InvalidOperationException($"The projection {type.FullName} named '{Consumer}' is no longer registered on this silo.");
        }

        var projection = services.GetServices<IProjection>().FirstOrDefault(p => handler.GetProjectionName(p) == Consumer)
                         ?? throw new InvalidOperationException($"No projection named '{Consumer}' is registered on this silo.");
        _projectionType = projection.GetType();
        return projection;
    }
}

/// <summary>The session an entry was recorded under, rebuilt for applying it.</summary>
internal static class RecordedSession
{
    /// <summary>What decides whether two entries were recorded under one session; unlike the rebuilt session, an entry without a correlation keys as such.</summary>
    public static RecordedSessionCarrier KeyOf(EventStreamEntry entry) =>
        new(entry.CorrelationId, entry.CausationId, entry.ActorTenantId, entry.ActorUserId, entry.TenantId, entry.UserId);

    public static SessionContext Of(RecordedSessionCarrier carrier) => new(
        carrier.CorrelationId ?? Guid.CreateVersion7().ToString("N"),
        carrier.CausationId,
        null,
        carrier.ActorTenantId,
        carrier.ActorUserId,
        carrier.TenantId,
        carrier.UserId);

    public static SessionContext Of(EventStreamEntry entry) => new(
        entry.CorrelationId ?? Guid.CreateVersion7().ToString("N"),
        entry.CausationId,
        null,
        entry.ActorTenantId,
        entry.ActorUserId,
        entry.TenantId,
        entry.UserId);
}

/// <summary>The fields of an entry's recorded session, as they travel between grains and key a session run.</summary>
[GenerateSerializer]
[Alias("Stratara.Orleans.RecordedSessionCarrier")]
[Immutable]
internal sealed record RecordedSessionCarrier(
    [property: Id(0)] string? CorrelationId,
    [property: Id(1)] string? CausationId,
    [property: Id(2)] Guid ActorTenantId,
    [property: Id(3)] Guid ActorUserId,
    [property: Id(4)] Guid TenantId,
    [property: Id(5)] Guid? UserId);

/// <summary>
/// The scope a batch's entries are applied from, one per session run: an entry recorded under another session than the
/// one before it opens a fresh scope, sets its session there first and only then resolves what applies it; an entry
/// of the same run reuses them, with its own session set again. The last scope is disposed with the runs.
/// </summary>
internal sealed class SessionRuns<TRun>(IServiceScopeFactory scopeFactory, Func<IServiceProvider, TRun> resolve) : IAsyncDisposable
{
    private AsyncServiceScope? _scope;
    private RecordedSessionCarrier? _key;
    private ISessionContextProvider? _sessions;
    private TRun? _run;

    public async ValueTask<TRun> EnterAsync(EventStreamEntry entry)
    {
        var key = RecordedSession.KeyOf(entry);
        if (_scope is not null && _sessions is { } sessions && _run is { } run && key == _key)
        {
            sessions.Set(RecordedSession.Of(entry));
            return run;
        }

        await DisposeAsync();
        var opened = scopeFactory.CreateAsyncScope();
        _scope = opened;
        _sessions = opened.ServiceProvider.GetRequiredService<ISessionContextProvider>();
        _sessions.Set(RecordedSession.Of(entry));
        _run = resolve(opened.ServiceProvider);
        _key = key;
        return _run;
    }

    public async ValueTask DisposeAsync()
    {
        if (_scope is { } scope)
        {
            _scope = null;
            _key = null;
            _run = default;
            await scope.DisposeAsync();
        }
    }
}

/// <summary>The key of a store-reading grain: <c>consumer/partition</c>.</summary>
internal static class StoreReaderGrainKey
{
    public static string Of(string consumer, int partition) => $"{consumer}/{partition}";

    public static (string Consumer, int Partition) Parse(string key)
    {
        var separator = key.LastIndexOf('/');
        return (key[..separator], int.Parse(key[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>A set of grains the bundle dispatcher nudges after a commit, keyed by partition, and the consumer names they checkpoint under.</summary>
internal interface INudgeTarget
{
    IReadOnlyList<string> ConsumerNames { get; }

    Task NudgeAsync(IGrainFactory grainFactory, int partition);

    Task EnsureRunningAsync(IGrainFactory grainFactory, int partition);

    /// <summary>
    /// Stops the target's readers of the partition, so their checkpoints can be changed behind them. A target whose
    /// readers cannot be stopped — one that stands for a consumer of its own in a test — does nothing.
    /// </summary>
    Task PauseAsync(IGrainFactory grainFactory, int partition) => Task.CompletedTask;

    /// <summary>Starts them again, from whatever the checkpoints now say.</summary>
    Task ResumeAsync(IGrainFactory grainFactory, int partition) => Task.CompletedTask;
}

/// <summary>Every registered projection's grain for the partition.</summary>
internal sealed class ProjectionNudgeTarget(IProjectionHandler projectionHandler, IEnumerable<IProjection> projections) : INudgeTarget
{
    private readonly List<string> _names = [.. projections.Select(projectionHandler.GetProjectionName).Distinct()];

    public IReadOnlyList<string> ConsumerNames => _names;

    /// <summary>
    /// Wakes every projection of the partition. A wake-up that cannot even be sent is collected rather than thrown
    /// at once, so one projection the call fails for does not cost the others behind it their wake-up; the
    /// dispatcher logs what failed.
    /// </summary>
    /// <exception cref="AggregateException">A wake-up could not be sent for one or more projections.</exception>
    public Task NudgeAsync(IGrainFactory grainFactory, int partition)
    {
        List<Exception>? failures = null;
        foreach (var name in _names)
        {
            try
            {
                grainFactory.GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(name, partition)).NudgeAsync().Ignore();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures is null ? Task.CompletedTask : Task.FromException(new AggregateException(failures));
    }

    public async Task EnsureRunningAsync(IGrainFactory grainFactory, int partition)
    {
        foreach (var name in _names)
        {
            await grainFactory.GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(name, partition)).EnsureRunningAsync();
        }
    }

    public Task PauseAsync(IGrainFactory grainFactory, int partition) =>
        StoreReaderPause.PauseAllAsync([.. _names.Select(name => Reader(grainFactory, name, partition))]).AsTask();

    public Task ResumeAsync(IGrainFactory grainFactory, int partition) =>
        StoreReaderPause.ResumeAllAsync([.. _names.Select(name => Reader(grainFactory, name, partition))]);

    private static PausedReader Reader(IGrainFactory grainFactory, string name, int partition)
    {
        var key = StoreReaderGrainKey.Of(name, partition);
        var grain = grainFactory.GetGrain<IProjectionGrain>(key);
        return new PausedReader(key, grain.PauseAsync, grain.ResumeAsync);
    }
}

/// <summary>
/// Brings every store-reading grain up once the silo is active, one per target and partition — a stage
/// of the silo's own lifecycle, so the order in which the host registered the silo and the composites
/// does not matter.
/// </summary>
internal sealed class StoreReaderGrainStarter(
    IServiceScopeFactory scopeFactory,
    IGrainFactory grainFactory,
    IOptions<CommitOrderOptions> commitOrder) : ILifecycleParticipant<global::Orleans.Runtime.ISiloLifecycle>
{
    public void Participate(global::Orleans.Runtime.ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(StoreReaderGrainStarter), ServiceLifecycleStage.Active, StartAsync);

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        foreach (var target in scope.ServiceProvider.GetServices<INudgeTarget>())
        {
            for (var partition = 0; partition < commitOrder.Value.PartitionCount; partition++)
            {
                await target.EnsureRunningAsync(grainFactory, partition);
            }
        }
    }

}
