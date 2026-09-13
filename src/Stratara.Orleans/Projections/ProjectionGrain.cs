using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Polly.Registry;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.CommitOrder;
using Stratara.Projections.Abstractions;
using Stratara.Resilience;

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
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Orleans:Projections";

    /// <summary>How many entries one read from the store returns.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>The safety net: how often a grain reads the store without having been nudged.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the cluster brings a lost grain back; not shorter than the minimum reminder period.</summary>
    public TimeSpan KeepAlivePeriod { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>One grain per projection and partition, keyed <c>projection/partition</c>.</summary>
internal interface IProjectionGrain : IGrainWithStringKey
{
    Task EnsureRunningAsync();

    /// <summary>A commit happened in this partition; read now rather than at the next poll.</summary>
    Task NudgeAsync();

    /// <summary>Reads and applies until the store has nothing newer; returns how many entries were applied.</summary>
    Task<int> CatchUpAsync();

    Task<long> PositionAsync();

    /// <summary>Stops reading until <see cref="ResumeAsync"/>; returns once no batch is in flight, which the turn guarantees.</summary>
    Task PauseAsync();

    /// <summary>Reads again, from whatever the checkpoint now says.</summary>
    Task ResumeAsync();
}

/// <summary>
/// Reads its partition of the event store in commit order from its checkpoint and applies each
/// entry to its projection through the framework's projection handler, under the session recorded
/// with the entry. The grain's turn is the serialisation: one batch at a time, so two facts about
/// one aggregate never apply concurrently. The checkpoint moves only past entries that applied; an
/// entry that fails — a missing prerequisite past the retry policy, or any other failure — stops the
/// batch where it is, and the next nudge or poll tries again from there.
/// </summary>
internal sealed class ProjectionGrain(
    IServiceScopeFactory scopeFactory,
    IEventMapperFactory eventMapperFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<ProjectionGrainOptions> options) : Grain, IProjectionGrain, IRemindable
{
    private const string KeepAliveReminder = "keep-alive";

    private readonly ProjectionGrainOptions _options = options.Value;
    private string _projection = string.Empty;
    private StoreReaderLoop _loop = null!;
    private IGrainTimer? _poll;
    private bool _paused;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var (projection, partition) = StoreReaderGrainKey.Parse(this.GetPrimaryKeyString());
        _projection = projection;
        _loop = new StoreReaderLoop(scopeFactory, pipelineProvider.GetPipeline(ResilienceNames.PrecedingFact), projection, partition, _options.BatchSize);
        _poll ??= this.RegisterGrainTimer(
            _ => CatchUpAsync(),
            new GrainTimerCreationOptions
            {
                DueTime = _options.PollInterval,
                Period = _options.PollInterval,
                Interleave = false,
                KeepAlive = true,
            });
        return base.OnActivateAsync(cancellationToken);
    }

    public Task EnsureRunningAsync() => this.RegisterOrUpdateReminder(KeepAliveReminder, _options.KeepAlivePeriod, _options.KeepAlivePeriod);

    public Task NudgeAsync() => CatchUpAsync();

    public Task<int> CatchUpAsync() => _paused || replayState.IsReplayActive ? Task.FromResult(0) : _loop.CatchUpAsync(ApplyEntryAsync);

    public Task<long> PositionAsync() => _loop.PositionAsync();

    public Task PauseAsync()
    {
        _paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        _paused = false;
        return CatchUpAsync();
    }

    Task IRemindable.ReceiveReminder(string reminderName, TickStatus status) => CatchUpAsync();

    private async Task ApplyEntryAsync(EventStreamEntry entry, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<ISessionContextProvider>().Set(RecordedSession.Of(entry));

        var handler = services.GetRequiredService<IProjectionHandler>();
        var projection = services.GetServices<IProjection>().FirstOrDefault(p => handler.GetProjectionName(p) == _projection)
                         ?? throw new InvalidOperationException($"No projection named '{_projection}' is registered on this silo.");

        var events = await eventMapperFactory.MapToEventsAsync([entry], cancellationToken);
        var relevant = handler.GetRelevantEventTypeNames(projection);
        var relevantEvents = events.Where(e => relevant.Contains(e.EventTypeName)).ToList();
        if (relevantEvents.Count == 0)
        {
            return;
        }

        await handler.ProjectAsync(projection, relevantEvents, cancellationToken);
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

/// <summary>Brings every store-reading grain up when the silo starts, one per target and partition.</summary>
internal sealed class StoreReaderGrainStarter(
    IServiceScopeFactory scopeFactory,
    IGrainFactory grainFactory,
    IOptions<CommitOrderOptions> commitOrder) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
