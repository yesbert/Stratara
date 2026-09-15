using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Polly.Registry;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Resilience;

namespace Stratara.Orleans.Projections;

/// <summary>How a store-reading grain reads: the batch it asks for, its poll and its keep-alive.</summary>
/// <param name="BatchSize">How many entries one read from the store returns.</param>
/// <param name="PollInterval">How often the grain reads the store without having been nudged.</param>
/// <param name="KeepAlivePeriod">How often the cluster brings a lost grain back.</param>
internal sealed record StoreReaderSettings(int BatchSize, TimeSpan PollInterval, TimeSpan KeepAlivePeriod);

/// <summary>
/// What every store-reading grain does, whatever it applies: parse its key into consumer and
/// partition, run one catch-up loop at a time over its partition, read when nudged, when its poll
/// fires and when its keep-alive reminder arrives, and wait for a running loop before it deactivates
/// so a successor never applies beside it. A derived grain says what applying a batch means and when
/// reading is suspended.
/// </summary>
internal abstract class StoreReaderGrain(
    IServiceScopeFactory scopeFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    StoreReaderSettings settings) : Grain, IRemindable
{
    private const string KeepAliveReminder = "keep-alive";

    private StoreReaderLoop? _loop;
    private IGrainTimer? _poll;

    /// <summary>The consumer this grain reads for, from its key.</summary>
    protected string Consumer { get; private set; } = string.Empty;

    /// <summary>Where each batch resolves its services.</summary>
    protected IServiceScopeFactory ScopeFactory => scopeFactory;

    /// <summary>The grain's catch-up loop.</summary>
    /// <exception cref="InvalidOperationException">The grain has not been activated.</exception>
    protected StoreReaderLoop Loop => _loop ?? throw new InvalidOperationException("The grain has not been activated.");

    /// <summary>Whether reading stops at the next batch boundary; a replay suspends every reader.</summary>
    protected virtual bool Suspended => replayState.IsReplayActive;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var (consumer, partition) = StoreReaderGrainKey.Parse(this.GetPrimaryKeyString());
        Consumer = consumer;
        _loop = new StoreReaderLoop(scopeFactory, pipelineProvider.GetPipeline(ResilienceNames.PrecedingFact), consumer, partition, settings.BatchSize);
        _poll ??= this.RegisterGrainTimer(
            _ => RequestCatchUp(),
            new GrainTimerCreationOptions
            {
                DueTime = settings.PollInterval,
                Period = settings.PollInterval,
                Interleave = false,
                KeepAlive = true,
            });
        return base.OnActivateAsync(cancellationToken);
    }

    public Task EnsureRunningAsync() => this.RegisterOrUpdateReminder(KeepAliveReminder, settings.KeepAlivePeriod, settings.KeepAlivePeriod);

    public Task NudgeAsync()
    {
        RequestCatchUp();
        return Task.CompletedTask;
    }

    public Task<int> CatchUpAsync() => RequestCatchUp();

    public Task<long> PositionAsync() => Loop.PositionAsync();

    Task IRemindable.ReceiveReminder(string reminderName, TickStatus status) => RequestCatchUp();

    /// <summary>A loop a nudge started holds no request; the activation waits for it so a successor never applies beside it.</summary>
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await Loop.WaitForRunningAsync(cancellationToken);
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    /// <summary>Applies a batch in order and returns the index of the first entry that did not apply.</summary>
    protected abstract Task<int> ApplyBatchAsync(CommittedBatch batch);

    private Task<int> RequestCatchUp() => Loop.RequestCatchUp(ApplyBatchAsync, () => Suspended);
}
