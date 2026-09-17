using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Polly;
using Polly.Retry;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// The silo's heavy workers. A pool grain accepts a hand-over, leases it and hands the unit here; the unit runs on
/// one of <see cref="HeavyWorkGrain.MaxLocalWorkers"/> workers, off the activation's scheduler, so a handler that
/// computes without awaiting anything occupies one worker and nothing else — the silo's other units keep running,
/// and the pool keeps accepting and leasing hand-overs while it computes. The permit a unit runs under is taken
/// here, on the worker, and renewed from a timer of its own.
/// </summary>
/// <remarks>
/// The workers are loops over a channel, started when the host starts, as
/// <see cref="IntentCompletionQueue"/>'s flush loop is: a hosted service has no synchronization context, so a loop
/// runs on the thread pool and the unit it invokes runs there too. A unit handed in after the host stopped the
/// runner runs on the caller, so nothing is lost to the order in which hosted services stop.
/// </remarks>
internal sealed class HeavyWorkRunner : IHostedService
{
    private readonly Channel<Unit> _queued = Channel.CreateUnbounded<Unit>();
    private readonly IGrainFactory _grainFactory;
    private readonly ILocalSiloDetails _localSilo;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HeavyWorkRunner> _logger;
    private readonly TimeSpan _renewal;
    private readonly ResiliencePipeline<bool> _acquirePermit;
    private Task[]? _workers;

    public HeavyWorkRunner(
        IGrainFactory grainFactory,
        IOptions<HeavyWorkOptions> options,
        ILocalSiloDetails localSilo,
        TimeProvider timeProvider,
        ILogger<HeavyWorkRunner> logger)
    {
        _grainFactory = grainFactory;
        _localSilo = localSilo;
        _timeProvider = timeProvider;
        _logger = logger;
        _renewal = options.Value.PermitLease / 2;
        _acquirePermit = new ResiliencePipelineBuilder<bool>()
            .AddRetry(new RetryStrategyOptions<bool>
            {
                ShouldHandle = new PredicateBuilder<bool>().HandleResult(false),
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Constant,
                Delay = options.Value.PermitRetry,
            })
            .Build();
    }

    /// <summary>
    /// Runs <paramref name="unit"/> on a worker, under a permit, and completes when it has run or thrown. The unit
    /// waits for a worker and for a permit under the lease its pool grain started, so a unit that waits counts as
    /// running.
    /// </summary>
    public Task RunAsync(Func<Task> unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new Unit(unit, completion, cancellationToken);
        return _queued.Writer.TryWrite(queued) ? completion.Task : UnderPermitAsync(unit, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workers ??= [.. Enumerable.Range(0, HeavyWorkGrain.MaxLocalWorkers).Select(_ => WorkAsync())];
        return Task.CompletedTask;
    }

    /// <summary>Ends the workers once what they hold has run, within the host's shutdown budget.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queued.Writer.TryComplete();
        if (_workers is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            _workers = null;
        }
    }

    private async Task WorkAsync()
    {
        var reader = _queued.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var unit))
            {
                try
                {
                    await UnderPermitAsync(unit.Run, unit.Cancellation);
                    unit.Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    unit.Completion.TrySetException(ex);
                }
            }
        }
    }

    /// <summary>
    /// Runs the unit under a permit. The release after it cannot change the unit's outcome: a release that fails is
    /// logged, and the permit's lease releases it.
    /// </summary>
    private async Task UnderPermitAsync(Func<Task> run, CancellationToken cancellationToken)
    {
        var permits = _grainFactory.GetGrain<IHeavyWorkPermitGrain>(0);
        var unitId = Guid.NewGuid();
        await _acquirePermit.ExecuteAsync(async _ => await permits.TryAcquireAsync(unitId, _localSilo.SiloAddress), cancellationToken);

        var renewal = new PermitRenewal(permits, unitId, _localSilo.SiloAddress, _logger);
        var renewing = _timeProvider.CreateTimer(static state => ((PermitRenewal)state!).Tick(), renewal, _renewal, _renewal);
        try
        {
            await run();
        }
        finally
        {
            await renewing.DisposeAsync();
            await renewal.SettleAsync();
            try
            {
                await permits.ReleaseAsync(unitId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogPermitReleaseFailed(ex, unitId);
            }
        }
    }

    private sealed record Unit(Func<Task> Run, TaskCompletionSource Completion, CancellationToken Cancellation);
}
