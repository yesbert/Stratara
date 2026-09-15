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
internal sealed class ProjectionGrain(
    IServiceScopeFactory scopeFactory,
    IEventMapperFactory eventMapperFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<ProjectionGrainOptions> options,
    Microsoft.Extensions.Logging.ILogger<ProjectionGrain> logger)
    : StoreReaderGrain(scopeFactory, replayState, pipelineProvider, new StoreReaderSettings(options.Value.BatchSize, options.Value.PollInterval, options.Value.KeepAlivePeriod), logger),
        IProjectionGrain
{
    private HashSet<string>? _relevant;
    private Type? _projectionType;
    private bool _paused;

    protected override bool Suspended => _paused || base.Suspended;

    /// <summary>
    /// Pauses, waits for a running loop to end, and forgets the cached position: whoever pauses is
    /// about to change the checkpoint behind the grain's back.
    /// </summary>
    public async Task PauseAsync()
    {
        _paused = true;
        await Loop.WaitForRunningAsync(CancellationToken.None);
        Loop.Invalidate();
    }

    public Task ResumeAsync()
    {
        _paused = false;
        return NudgeAsync();
    }

    /// <summary>
    /// One scope, one projection instance and one relevant-event set for the batch; per entry, the
    /// recorded session and the retry policy, as the <c>projections</c> guarantees require.
    /// </summary>
    protected override async Task<int> ApplyBatchAsync(CommittedBatch batch, CancellationToken cancellationToken)
    {
        using var scope = ScopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var sessions = services.GetRequiredService<ISessionContextProvider>();
        var handler = services.GetRequiredService<IProjectionHandler>();
        var projection = ResolveProjection(services, handler);
        _relevant ??= new HashSet<string>(handler.GetRelevantEventTypeNames(projection), StringComparer.Ordinal);

        return await Loop.ApplyEachAsync(batch, async (entry, entryToken) =>
        {
            sessions.Set(RecordedSession.Of(entry));

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
    public static SessionContext Of(EventStreamEntry entry) => new(
        entry.CorrelationId ?? Guid.CreateVersion7().ToString("N"),
        entry.CausationId,
        null,
        entry.ActorTenantId,
        entry.ActorUserId,
        entry.TenantId,
        entry.UserId);
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

/// <summary>A set of grains the bundle dispatcher nudges after a commit, keyed by partition.</summary>
internal interface INudgeTarget
{
    Task NudgeAsync(IGrainFactory grainFactory, int partition);

    Task EnsureRunningAsync(IGrainFactory grainFactory, int partition);
}

/// <summary>Every registered projection's grain for the partition.</summary>
internal sealed class ProjectionNudgeTarget(IProjectionHandler projectionHandler, IEnumerable<IProjection> projections) : INudgeTarget
{
    private readonly List<string> _names = [.. projections.Select(projectionHandler.GetProjectionName).Distinct()];

    public Task NudgeAsync(IGrainFactory grainFactory, int partition)
    {
        foreach (var name in _names)
        {
            grainFactory.GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(name, partition)).NudgeAsync().Ignore();
        }

        return Task.CompletedTask;
    }

    public async Task EnsureRunningAsync(IGrainFactory grainFactory, int partition)
    {
        foreach (var name in _names)
        {
            await grainFactory.GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(name, partition)).EnsureRunningAsync();
        }
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
