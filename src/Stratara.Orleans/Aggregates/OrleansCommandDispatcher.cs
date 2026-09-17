using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Diagnostics;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// The durable-intent shape behind <c>ICommandOutboxDispatcher</c>: the command is recorded in
/// durable storage before the call returns, then handed to its grain without waiting for the
/// handler. The record is removed once the handler has completed. A record whose hand-over has not
/// been renewed within <see cref="OrleansDispatchOptions.IntentGrace"/> is one whose hand-off was
/// lost — a host that died between recording and completing — and the drain resumes it, a bounded
/// number of times.
/// </summary>
/// <remarks>
/// This keeps the interface's promise the same way the bus does — what is accepted is stored — and
/// changes what "accepted" means for a host that dies: the bus loses the message it had not yet
/// published, this loses nothing and may run a handler twice. Handlers are already expected to
/// tolerate a second delivery. While a replay is active, commands are recorded and not handed over,
/// as the bus dispatcher does. Hand-overs to one aggregate from one scope keep their order; a heavy command runs
/// outside its aggregate's turn and order, so its hand-over neither waits for the calls before it nor holds the ones after it.
/// </remarks>
internal sealed class OrleansCommandDispatcher(
    IntentRecorder recorder,
    IntentHandOver handOver,
    IntentResumer resumer,
    ISessionContextProvider sessionContextProvider,
    IProjectionReplayState replayState,
    AggregateSendLane lane,
    ReplaySuspensionTracker replaySuspension,
    ILogger<OrleansCommandDispatcher> logger) : ICommandOutboxDispatcher
{
    /// <inheritdoc/>
    public async Task<Guid> EnqueueCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : ICommand
    {
        var session = sessionContextProvider.Current ?? throw new InvalidOperationException("Session context is not set");
        var intentId = Guid.CreateVersion7();
        var heavy = command is IHeavyCommand;
        var aggregateId = (command as IAggregateScopedCommand)?.AggregateId;

        var recorded = recorder.RecordAsync(intentId, command, session, aggregateId, heavy, cancellationToken);
        var issued = lane.SendAsync(AggregateSendLane.KeyOf(intentId, aggregateId, heavy), recorded, payload =>
            replayState.IsReplayActive ? Task.CompletedTask : handOver.HandOverAsync(intentId, payload, heavy, aggregateId));

        IntentHandOver.Observe(await issued, logger, intentId, aggregateId, heavy);
        return intentId;
    }

    /// <summary>The drain's entry point: resumes up to as many due commands as the drain handed over entries.</summary>
    /// <inheritdoc/>
    public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) =>
        ResumeDueAsync(Math.Max(1, outboxEntries.Count()), cancellationToken);

    /// <summary>Resumes up to <paramref name="batchSize"/> due commands, unless a replay is active; a hold and its end are logged once each.</summary>
    public Task<int> ResumeDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (replayState.IsReplayActive)
        {
            replaySuspension.HeldBack(logger);
            return Task.FromResult(0);
        }

        replaySuspension.Released(logger);
        return resumer.ResumeDueAsync(batchSize, cancellationToken);
    }
}

/// <summary>
/// Hands a recorded command to the grain that runs it: heavy work, its aggregate — which accepts it into the
/// aggregate's order and returns before it runs — or a runner of its own.
/// </summary>
internal sealed class IntentHandOver(IGrainFactory grainFactory, IOptions<HeavyWorkOptions> heavyWork)
{
    private readonly int _pools = HeavyWorkGrain.PoolsFor(heavyWork.Value.ClusterWideLimit);

    public Task HandOverAsync(Guid intentId, AggregateCommandEnvelope payload, bool heavy, Guid? aggregateId)
    {
        if (heavy)
        {
            return grainFactory.GetGrain<IHeavyWorkGrain>(PoolOf(intentId)).ExecuteIntentAsync(intentId, payload);
        }

        return aggregateId is { } id
            ? grainFactory.GetGrain<IAggregateGrain>(id).AcceptIntentAsync(intentId, payload)
            : grainFactory.GetGrain<ICommandRunnerGrain>(intentId).ExecuteIntentAsync(payload);
    }

    /// <summary>The pool a heavy intent goes to: spread over the pools by its id.</summary>
    private long PoolOf(Guid intentId) => (long)(unchecked((uint)intentId.GetHashCode()) % (uint)_pools);

    /// <summary>
    /// Nobody waits for a hand-over, but a hand-over that fails is logged. A call that spans the whole unit — heavy
    /// work, or a command that names no aggregate — outlives the runtime's response timeout by design; its timeout
    /// says nothing about the unit and is not logged.
    /// </summary>
    public static void Observe(Task call, ILogger logger, Guid intentId, Guid? aggregateId, bool heavy)
    {
        var spansTheUnit = heavy || aggregateId is null;
        call.ContinueWith(
            (faulted, state) =>
            {
                var (log, id, aggregate, isHeavy, longCall) = ((ILogger, Guid, Guid?, bool, bool))state!;
                var failure = faulted.Exception!.GetBaseException();
                if (longCall && failure is TimeoutException)
                {
                    return;
                }

                log.LogHandOverFailed(failure, id, aggregate, isHeavy);
            },
            (logger, intentId, aggregateId, heavy, spansTheUnit),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Ignore();
    }
}

/// <summary>
/// The bounded resume. A due command whose attempts have reached the bound the host configures for bus
/// messages is kept for an operator; any other due command is claimed — its hand-over stamped and its
/// attempt counted — and handed over in the order of the aggregate the record names. A hand-over that
/// cannot be issued is recorded as the command's failure and does not end the pass.
/// </summary>
internal sealed class IntentResumer(
    ICommandIntentStore intents,
    IntentHandOver handOver,
    AggregateSendLane lane,
    IOptions<OrleansDispatchOptions> options,
    IOptions<MessageRetryOptions> retry,
    TimeProvider timeProvider,
    ILogger<IntentResumer> logger)
{
    private readonly TimeSpan _grace = options.Value.IntentGrace;
    private readonly int _maxAttempts = retry.Value.MaxDeliveryAttempts;

    /// <summary>
    /// A resumer for a silo that runs the drain but does not dispatch commands itself: the grace and the attempt
    /// bound come from the options registered there, as on a dispatching host.
    /// </summary>
    public static IntentResumer Create(IServiceProvider services, ICommandIntentStore intents) =>
        services.GetService<IntentResumer>() ?? new IntentResumer(
            intents,
            new IntentHandOver(services.GetRequiredService<IGrainFactory>(), services.GetService<IOptions<HeavyWorkOptions>>() ?? Options.Create(new HeavyWorkOptions())),
            new AggregateSendLane(),
            services.GetService<IOptions<OrleansDispatchOptions>>() ?? Options.Create(new OrleansDispatchOptions()),
            services.GetService<IOptions<MessageRetryOptions>>() ?? Options.Create(new MessageRetryOptions()),
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            services.GetRequiredService<ILogger<IntentResumer>>());

    public async Task<int> ResumeDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var due = await intents.GetDueAsync(now - _grace, batchSize, cancellationToken);
        var resumed = 0;
        foreach (var intent in due)
        {
            if (intent.AttemptCount >= _maxAttempts)
            {
                await intents.KeepAsync(intent.Id, now, cancellationToken);
                ApplicationDiagnostics.Metrics.OrleansIntentKept.Add(1);
                logger.LogCommandKept(intent.Id, intent.AttemptCount, intent.LastFailure ?? "none recorded");
                continue;
            }

            if (!await intents.TryClaimAsync(intent.Id, intent.LastHandedOverAt, now, cancellationToken))
            {
                continue;
            }

            ApplicationDiagnostics.Metrics.OrleansIntentResumed.Add(1);
            logger.LogCommandResumed(intent.Id, intent.AttemptCount + 1);
            var payload = new AggregateCommandEnvelope(intent.Envelope.CommandTypeName, intent.Envelope.CommandJson, intent.Envelope.SessionContextJson);
            try
            {
                var issued = await lane.SendAsync(AggregateSendLane.KeyOf(intent.Id, intent.AggregateId, intent.Heavy), Task.FromResult(payload), issue =>
                    handOver.HandOverAsync(intent.Id, issue, intent.Heavy, intent.AggregateId));
                IntentHandOver.Observe(issued, logger, intent.Id, intent.AggregateId, intent.Heavy);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await intents.RecordFailureAsync(intent.Id, IntentFailure.Describe(ex), cancellationToken);
            }

            resumed++;
        }

        return resumed;
    }
}

/// <summary>How a failure is recorded with a command.</summary>
internal static class IntentFailure
{
    public static string Describe(Exception exception) => $"{exception.GetType().FullName}: {exception.Message}";
}

/// <summary>Settings for the Orleans-backed command dispatcher.</summary>
public sealed class OrleansDispatchOptions
{
    /// <summary>
    /// How long a recorded command may go without its hand-over being renewed before the drain hands it
    /// over again. A running handler renews its hand-over every third of this period, so a long handler is not
    /// handed over twice; a host that died stops renewing, and its commands are resumed after it.
    /// </summary>
    public TimeSpan IntentGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long completed intents are collected before one delete removes them. A host that dies
    /// inside the window resumes those intents after <see cref="IntentGrace"/> and runs their handlers
    /// a second time; keep it far below the grace.
    /// </summary>
    public TimeSpan CompletionWindow { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>How many completed intents one delete removes at most; a full batch ends the window early.</summary>
    public int CompletionBatchSize { get; set; } = 64;
}
