using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Polly.Registry;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Diagnostics;
using Stratara.Orleans.Projections;
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

/// <summary>
/// The saga reader of a release before 4.2.0: one grain per partition for all registered sagas, keyed
/// <c>sagas/partition</c>. Kept so that the keep-alive such a release registered, and the calls a silo of it still
/// makes in a rolling cluster, reach a grain that retires instead of one that reads.
/// </summary>
[Alias("Stratara.Orleans.ISagaGrain")]
internal interface ISagaGrain : IGrainWithStringKey
{
    [Alias("EnsureRunningAsync")]
    Task EnsureRunningAsync();

    /// <summary>A commit happened in this partition; the retired grain ignores it.</summary>
    [OneWay]
    [AlwaysInterleave]
    [Alias("NudgeAsync")]
    Task NudgeAsync();

    [Alias("CatchUpAsync")]
    Task<int> CatchUpAsync();

    [Alias("PositionAsync")]
    Task<long> PositionAsync();

    /// <summary>A held pause; the retired grain holds none.</summary>
    [AlwaysInterleave]
    [Alias("PauseHeldAsync")]
    Task PauseAsync(Guid pauser, TimeSpan lease);

    /// <summary>A renewed pause; the retired grain holds none.</summary>
    [AlwaysInterleave]
    [Alias("RenewPauseAsync")]
    Task RenewPauseAsync(Guid pauser, TimeSpan lease);

    /// <summary>A released pause; the retired grain holds none.</summary>
    [AlwaysInterleave]
    [Alias("ResumeHeldAsync")]
    Task ResumeAsync(Guid pauser);

    /// <summary>The pause of a silo of an older version; the retired grain holds none.</summary>
    [AlwaysInterleave]
    [Alias("PauseAsync")]
    Task PauseAsync();

    /// <summary>The resume of a silo of an older version; the retired grain holds none.</summary>
    [Alias("ResumeAsync")]
    Task ResumeAsync();
}

/// <summary>
/// The shared saga reader of a release before 4.2.0, retired: every saga reads with a checkpoint of its own in a
/// <see cref="SagaReaderGrain"/>. Brought back by the keep-alive such a release registered, or by a call of a silo that
/// still runs it, the grain unregisters its keep-alive, logs that it retired and reads nothing, as a reader beyond a
/// lowered partition count does. Its grain type stays what it was, so that the reminder resolves; the per-saga readers
/// have a grain type of their own, so that no silo of the earlier release activates one. The checkpoint the shared
/// reader left under <see cref="ConsumerName"/> stays where it was: it is where the sagas start.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
[SagasRolePlacementFilter]
internal sealed class SagaGrain(
    IServiceScopeFactory scopeFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<SagaGrainOptions> options,
    IOptions<CommitOrderOptions> commitOrder,
    Microsoft.Extensions.Logging.ILogger<SagaGrain> logger)
    : StoreReaderGrain(scopeFactory, replayState, pipelineProvider, new StoreReaderSettings(options.Value.BatchSize, options.Value.PollInterval, options.Value.KeepAlivePeriod, commitOrder.Value.PartitionCount), logger),
        ISagaGrain
{
    /// <summary>The consumer the shared reader kept its checkpoints under.</summary>
    public const string ConsumerName = "sagas";

    protected override StoreReaderRetirement RetiresAs => StoreReaderRetirement.Superseded;

    protected override void LogRetirement() => logger.LogSharedSagaReaderRetired(Partition);

    /// <summary>Never reached: the grain retires when it is activated.</summary>
    protected override Task<int> ApplyBatchAsync(CommittedBatch batch, CancellationToken cancellationToken) => Task.FromResult(0);
}
