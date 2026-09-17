using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Polly.Registry;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;
using Stratara.Sagas.Abstractions;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.Sagas;

/// <summary>Settings for the saga grains.</summary>
public sealed class SagaGrainOptions
{
    /// <summary>How many entries one read from the store returns.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>The safety net: how often the grain reads the store without having been nudged.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the cluster brings a lost grain back; not shorter than the minimum reminder period.</summary>
    public TimeSpan KeepAlivePeriod { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>One grain per partition for all registered sagas, keyed <c>sagas/partition</c>.</summary>
[Alias("Stratara.Orleans.ISagaGrain")]
internal interface ISagaGrain : IGrainWithStringKey
{
    [Alias("EnsureRunningAsync")]
    Task EnsureRunningAsync();

    /// <summary>A commit happened in this partition; interleaves with a running catch-up, which then reads once more.</summary>
    [OneWay]
    [AlwaysInterleave]
    [Alias("NudgeAsync")]
    Task NudgeAsync();

    [Alias("CatchUpAsync")]
    Task<int> CatchUpAsync();

    [Alias("PositionAsync")]
    Task<long> PositionAsync();

    /// <summary>
    /// Stops reading until <see cref="ResumeAsync"/>; returns once no batch is in flight. Interleaves with a running
    /// catch-up, which stops at its next batch boundary.
    /// </summary>
    [AlwaysInterleave]
    [Alias("PauseAsync")]
    Task PauseAsync();

    /// <summary>Reads again, from whatever the checkpoint now says; returns once the read is requested, not once it is done.</summary>
    [Alias("ResumeAsync")]
    Task ResumeAsync();
}

/// <summary>
/// The saga worker's job without the bus: reads its partition in commit order from a checkpoint and
/// hands each entry to the framework's saga manager, which dispatches it to every registered saga in
/// parallel and in order within each — the <c>sagas</c> guarantees, unchanged. Existing stateless
/// sagas run here without modification, and a fact a stateful process handles is handed to that
/// process's grain.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
[SagasRolePlacementFilter]
internal sealed class SagaGrain(
    IServiceScopeFactory scopeFactory,
    IEventMapperFactory eventMapperFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<SagaGrainOptions> options,
    IOptions<CommitOrderOptions> commitOrder,
    Microsoft.Extensions.Logging.ILogger<SagaGrain> logger)
    : StoreReaderGrain(scopeFactory, replayState, pipelineProvider, new StoreReaderSettings(options.Value.BatchSize, options.Value.PollInterval, options.Value.KeepAlivePeriod, commitOrder.Value.PartitionCount), logger),
        ISagaGrain
{
    public const string ConsumerName = "sagas";

    /// <summary>
    /// One scope, one saga manager and one process list per session run, resolved after the run's session is set, so
    /// a saga's dependency that takes its tenant when it is constructed takes the entry's; per entry, the recorded
    /// session. A fact a process handles travels to its grain with the session it was recorded under.
    /// </summary>
    protected override async Task<int> ApplyBatchAsync(CommittedBatch batch, CancellationToken cancellationToken)
    {
        await using var runs = new SessionRuns<(ISagaManager Sagas, List<ISagaProcess> Processes)>(ScopeFactory, services =>
            (services.GetRequiredService<ISagaManager>(), services.GetServices<ISaga>().OfType<ISagaProcess>().ToList()));

        return await Loop.ApplyEachAsync(batch, async (entry, entryToken) =>
        {
            var (sagas, processes) = await runs.EnterAsync(entry);
            var events = await eventMapperFactory.MapToEventsAsync([entry], entryToken);
            await sagas.HandleAsync(events, entryToken);

            foreach (var process in processes)
            {
                foreach (var @event in events.Where(process.Handles))
                {
                    var key = SagaProcessKey.Of(process.GetType().Name, process.CorrelationOf(@event));
                    await GrainFactory.GetGrain<ISagaProcessGrain>(key).HandleAsync(entry.StreamId, entry.Version, RecordedSession.KeyOf(entry), entryToken);
                }
            }
        }, cancellationToken);
    }
}

/// <summary>The saga grain for the partition.</summary>
internal sealed class SagaNudgeTarget : INudgeTarget
{
    public IReadOnlyList<string> ConsumerNames { get; } = [SagaGrain.ConsumerName];

    public Task NudgeAsync(IGrainFactory grainFactory, int partition)
    {
        grainFactory.GetGrain<ISagaGrain>(StoreReaderGrainKey.Of(SagaGrain.ConsumerName, partition)).NudgeAsync().Ignore();
        return Task.CompletedTask;
    }

    public Task EnsureRunningAsync(IGrainFactory grainFactory, int partition) =>
        grainFactory.GetGrain<ISagaGrain>(StoreReaderGrainKey.Of(SagaGrain.ConsumerName, partition)).EnsureRunningAsync();

    public Task PauseAsync(IGrainFactory grainFactory, int partition) =>
        StoreReaderPause.PauseAllAsync([Reader(grainFactory, partition)]).AsTask();

    public Task ResumeAsync(IGrainFactory grainFactory, int partition) =>
        StoreReaderPause.ResumeAllAsync([Reader(grainFactory, partition)]);

    private static PausedReader Reader(IGrainFactory grainFactory, int partition)
    {
        var key = StoreReaderGrainKey.Of(SagaGrain.ConsumerName, partition);
        var grain = grainFactory.GetGrain<ISagaGrain>(key);
        return new PausedReader(key, grain.PauseAsync, grain.ResumeAsync);
    }
}
