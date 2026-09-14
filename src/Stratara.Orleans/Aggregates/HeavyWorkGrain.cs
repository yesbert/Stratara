using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Polly;
using Polly.Retry;
using Orleans.GrainDirectory;

namespace Stratara.Orleans.Aggregates;

/// <summary>Settings for heavy work.</summary>
public sealed class HeavyWorkOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Orleans:HeavyWork";

    /// <summary>How many heavy units may run at once across the whole cluster.</summary>
    public int ClusterWideLimit { get; set; } = 8;

    /// <summary>How long a worker waits before asking for a permit again.</summary>
    public TimeSpan PermitRetry { get; set; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>A bounded pool of workers per silo; the key is ignored.</summary>
[Alias("Stratara.Orleans.IHeavyWorkGrain")]
internal interface IHeavyWorkGrain : IGrainWithIntegerKey
{
    [Alias("ExecuteIntentAsync")]
    Task ExecuteIntentAsync(Guid intentId, AggregateCommandEnvelope envelope);
}

/// <summary>One grain for the cluster that hands out the permits heavy work runs under.</summary>
[Alias("Stratara.Orleans.IHeavyWorkPermitGrain")]
internal interface IHeavyWorkPermitGrain : IGrainWithIntegerKey
{
    [Alias("TryAcquireAsync")]
    Task<bool> TryAcquireAsync();

    [Alias("ReleaseAsync")]
    Task ReleaseAsync();

    [Alias("InUseAsync")]
    Task<int> InUseAsync();
}

/// <summary>
/// Heavy work runs here rather than in the aggregate's grain, so a long unit does not hold an
/// aggregate's turn, and here rather than anywhere, so the number running is bounded: per silo by
/// the worker pool, and across the cluster by the permit grain — the limit a stateless worker's
/// pool alone cannot give. The intent was recorded before the hand-off and is completed after the
/// handler, so a crash in between is resumed like any other intent.
/// </summary>
[StatelessWorker(HeavyWorkGrain.MaxLocalWorkers)]
internal sealed class HeavyWorkGrain(IServiceScopeFactory scopeFactory, IOptions<HeavyWorkOptions> options) : Grain, IHeavyWorkGrain
{
    public const int MaxLocalWorkers = 8;

    private readonly ResiliencePipeline<bool> _acquirePermit = new ResiliencePipelineBuilder<bool>()
        .AddRetry(new RetryStrategyOptions<bool>
        {
            ShouldHandle = new PredicateBuilder<bool>().HandleResult(false),
            MaxRetryAttempts = int.MaxValue,
            BackoffType = DelayBackoffType.Constant,
            Delay = options.Value.PermitRetry,
        })
        .Build();

    public async Task ExecuteIntentAsync(Guid intentId, AggregateCommandEnvelope envelope)
    {
        var permits = GrainFactory.GetGrain<IHeavyWorkPermitGrain>(0);
        await _acquirePermit.ExecuteAsync(async _ => await permits.TryAcquireAsync());

        try
        {
            await CommandExecution.RunAsync(scopeFactory, envelope, intentId);
        }
        finally
        {
            await permits.ReleaseAsync();
        }
    }
}

/// <summary>The cluster-wide counter behind the permits: single activation, one call at a time.</summary>
[GrainDirectory(GrainDirectories.Durable)]
internal sealed class HeavyWorkPermitGrain(IOptions<HeavyWorkOptions> options) : Grain, IHeavyWorkPermitGrain
{
    private readonly int _limit = options.Value.ClusterWideLimit;
    private int _inUse;

    public Task<bool> TryAcquireAsync()
    {
        if (_inUse >= _limit)
        {
            return Task.FromResult(false);
        }

        _inUse++;
        return Task.FromResult(true);
    }

    public Task ReleaseAsync()
    {
        if (_inUse > 0)
        {
            _inUse--;
        }

        return Task.CompletedTask;
    }

    public Task<int> InUseAsync() => Task.FromResult(_inUse);
}
