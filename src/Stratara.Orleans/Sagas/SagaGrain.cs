using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Polly.Registry;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Orleans.Projections;
using Stratara.Resilience;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Sagas;

/// <summary>Settings for the saga grains.</summary>
public sealed class SagaGrainOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Orleans:Sagas";

    /// <summary>How many entries one read from the store returns.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>The safety net: how often the grain reads the store without having been nudged.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the cluster brings a lost grain back; not shorter than the minimum reminder period.</summary>
    public TimeSpan KeepAlivePeriod { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>One grain per partition for all registered sagas, keyed <c>sagas/partition</c>.</summary>
internal interface ISagaGrain : IGrainWithStringKey
{
    Task EnsureRunningAsync();

    Task NudgeAsync();

    Task<int> CatchUpAsync();

    Task<long> PositionAsync();
}

/// <summary>
/// The saga worker's job without the bus: reads its partition in commit order from a checkpoint and
/// hands each entry to the framework's saga manager, which dispatches it to every registered saga in
/// parallel and in order within each — the <c>sagas</c> guarantees, unchanged. Existing stateless
/// sagas run here without modification. The grain's turn replaces the per-process bucket lock with a
/// cluster-wide one.
/// </summary>
internal sealed class SagaGrain(
    IServiceScopeFactory scopeFactory,
    IEventMapperFactory eventMapperFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<SagaGrainOptions> options) : Grain, ISagaGrain, IRemindable
{
    public const string Consumer = "sagas";
    private const string KeepAliveReminder = "keep-alive";

    private readonly SagaGrainOptions _options = options.Value;
    private StoreReaderLoop? _loop;
    private IGrainTimer? _poll;

    private StoreReaderLoop Loop => _loop ?? throw new InvalidOperationException("The grain has not been activated.");

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var (_, partition) = StoreReaderGrainKey.Parse(this.GetPrimaryKeyString());
        _loop = new StoreReaderLoop(scopeFactory, pipelineProvider.GetPipeline(ResilienceNames.PrecedingFact), Consumer, partition, _options.BatchSize);
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

    public Task<int> CatchUpAsync() => replayState.IsReplayActive ? Task.FromResult(0) : Loop.CatchUpAsync(DispatchEntryAsync);

    public Task<long> PositionAsync() => Loop.PositionAsync();

    Task IRemindable.ReceiveReminder(string reminderName, TickStatus status) => CatchUpAsync();

    private async Task DispatchEntryAsync(EventStreamEntry entry, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<ISessionContextProvider>().Set(RecordedSession.Of(entry));
        var events = await eventMapperFactory.MapToEventsAsync([entry], cancellationToken);
        await services.GetRequiredService<ISagaManager>().HandleAsync(events, cancellationToken);

        foreach (var process in services.GetServices<ISaga>().OfType<ISagaProcess>())
        {
            foreach (var @event in events.Where(process.Handles))
            {
                var key = SagaProcessKey.Of(process.GetType().Name, process.CorrelationOf(@event));
                await GrainFactory.GetGrain<ISagaProcessGrain>(key).HandleAsync(entry.StreamId, entry.Version);
            }
        }
    }
}

/// <summary>The saga grain for the partition.</summary>
internal sealed class SagaNudgeTarget : INudgeTarget
{
    public Task NudgeAsync(IGrainFactory grainFactory, int partition)
    {
        grainFactory.GetGrain<ISagaGrain>(StoreReaderGrainKey.Of(SagaGrain.Consumer, partition)).NudgeAsync().Ignore();
        return Task.CompletedTask;
    }

    public Task EnsureRunningAsync(IGrainFactory grainFactory, int partition) =>
        grainFactory.GetGrain<ISagaGrain>(StoreReaderGrainKey.Of(SagaGrain.Consumer, partition)).EnsureRunningAsync();
}
