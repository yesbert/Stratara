using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
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
[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "DI-resolved sealed internal dispatcher; primary-constructor parameters reflect intrinsic " +
                    "framework dependencies (recorder, hand-over, resumer, session, replay state, send lane, replay suspension, time provider, logger) and are not a hand-called API surface.")]
internal sealed class OrleansCommandDispatcher(
    IntentRecorder recorder,
    IntentHandOver handOver,
    IntentResumer resumer,
    ISessionContextProvider sessionContextProvider,
    IProjectionReplayState replayState,
    AggregateSendLane lane,
    ReplaySuspensionTracker replaySuspension,
    TimeProvider timeProvider,
    ILogger<OrleansCommandDispatcher> logger) : ICommandOutboxDispatcher
{
    private static readonly TimeSpan OrderStep = TimeSpan.FromMilliseconds(1);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastRecordedAt = new();

    /// <inheritdoc/>
    /// <remarks>
    /// The record's time is taken here, before anything is awaited, so a command recorded more slowly than the one the
    /// scope dispatched after it still carries the earlier time and is resumed first.
    /// </remarks>
    public async Task<Guid> EnqueueCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : ICommand
    {
        var session = sessionContextProvider.Current ?? throw new SessionRequiredException("Session context is not set");
        var now = timeProvider.GetUtcNow();
        var intentId = Guid.CreateVersion7(now);
        var heavy = command is IHeavyCommand;
        var aggregateId = (command as IAggregateScopedCommand)?.AggregateId;
        var key = AggregateSendLane.KeyOf(intentId, aggregateId, heavy);
        var dispatch = new IntentDispatch(intentId, session, aggregateId, heavy, RecordedAt(key, now, ordered: key != intentId));

        var recorded = recorder.RecordAsync(dispatch, command, cancellationToken);
        var issued = lane.SendAsync(key, recorded, payload =>
            replayState.IsReplayActive ? Task.CompletedTask : handOver.HandOverAsync(intentId, payload, heavy, aggregateId));

        IntentHandOver.Observe(await issued, logger, intentId, aggregateId, heavy);
        return intentId;
    }

    /// <summary>The drain's entry point: resumes up to as many due commands as the drain handed over entries.</summary>
    /// <inheritdoc/>
    public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) =>
        ResumeDueAsync(Math.Max(1, outboxEntries.Count()), cancellationToken);

    /// <summary>Resumes up to <paramref name="batchSize"/> due commands, unless a replay is active; a hold and its end are logged once each.</summary>
    public Task<ResumePass> ResumeDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (replayState.IsReplayActive)
        {
            replaySuspension.HeldBack(logger);
            return Task.FromResult(ResumePass.None);
        }

        replaySuspension.Released(logger);
        return resumer.ResumeDueAsync(batchSize, cancellationToken);
    }

    /// <summary>How many lanes the scope still remembers a time for.</summary>
    internal int RememberedLanes
    {
        get
        {
            lock (_gate)
            {
                return _lastRecordedAt.Count;
            }
        }
    }

    /// <summary>
    /// The record's time: <paramref name="now"/>, or one step past the last time this scope took for the same ordered
    /// lane where that is not earlier — a step every provider keeps apart after its rounding. Only a lane that keeps an
    /// order, an aggregate's, is remembered, and only while its last time is within a step of now: past that, now is
    /// later anyway, so a long-lived scope remembers only the lanes it dispatched to in the last step.
    /// </summary>
    private DateTimeOffset RecordedAt(Guid key, DateTimeOffset now, bool ordered)
    {
        if (!ordered)
        {
            return now;
        }

        lock (_gate)
        {
            foreach (var stale in _lastRecordedAt.Where(lane => lane.Value + OrderStep <= now).Select(lane => lane.Key).ToList())
            {
                _lastRecordedAt.Remove(stale);
            }

            var recordedAt = _lastRecordedAt.TryGetValue(key, out var last) && now < last + OrderStep ? last + OrderStep : now;
            _lastRecordedAt[key] = recordedAt;
            return recordedAt;
        }
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

/// <summary>What one resume pass did: how many commands it handed over, and whether it found as many due as it asked for.</summary>
internal readonly record struct ResumePass(int Resumed, bool Full)
{
    public static ResumePass None => new(0, false);
}

/// <summary>
/// The bounded resume. A due command that has reached either bound the host configures for bus messages — the delivery
/// bound, which counts the hand-over of its dispatch as the first attempt, or the conflict bound — is kept for an operator; a due command whose record does not verify under the host's integrity mode is
/// kept at once under strict mode, with the reason, and resumed with the failure logged under permissive mode; every
/// other due command is claimed in one call — its hand-over stamped and its attempt counted — and handed over with the
/// claim's stamp, in the order it was read, in the order of the aggregate the record names. A hand-over that cannot be issued is recorded as
/// the command's failure and does not end the pass.
/// </summary>
[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "DI-resolved sealed internal resumer; primary-constructor parameters reflect intrinsic " +
                    "framework dependencies (intent store, hand-over, send lane, dispatch and retry options, time provider, logger, signer, integrity options) and are not a hand-called API surface.")]
internal sealed class IntentResumer(
    ICommandIntentStore intents,
    IntentHandOver handOver,
    AggregateSendLane lane,
    IOptions<OrleansDispatchOptions> options,
    IOptions<MessageRetryOptions> retry,
    TimeProvider timeProvider,
    ILogger<IntentResumer> logger,
    IBusEnvelopeSigner? signer = null,
    IOptions<BusEnvelopeIntegrityOptions>? integrity = null)
{
    private const string UnsignedReason = "The recorded command carries no signature and the integrity mode is Strict.";
    private const string InvalidReason = "The recorded command's signature does not verify and the integrity mode is Strict.";
    private const string RoutingReason = "The recorded command's row disagrees with the signed envelope about where it runs, and the integrity mode is Strict.";

    private readonly TimeSpan _grace = options.Value.IntentGrace;
    private readonly int _maxAttempts = retry.Value.MaxDeliveryAttempts;
    private readonly int _maxConflicts = retry.Value.MaxConflictRequeues;
    private readonly BusEnvelopeIntegrityMode _mode = integrity?.Value.Mode ?? BusEnvelopeIntegrityMode.Off;

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
            services.GetRequiredService<ILogger<IntentResumer>>(),
            services.GetService<IBusEnvelopeSigner>(),
            services.GetService<IOptions<BusEnvelopeIntegrityOptions>>());

    /// <summary>
    /// One pass. The claim's time is truncated to the millisecond before it is stamped, so the stamp every hand-over
    /// carries is the one the store keeps, and the receiver's fencing renewal compares equal values.
    /// </summary>
    public async Task<ResumePass> ResumeDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var claimedAt = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var due = await intents.GetDueAsync(now - _grace, batchSize, cancellationToken);
        var claimable = new List<RecordedIntent>(due.Count);
        foreach (var intent in due)
        {
            if (IsExhausted(intent))
            {
                await intents.KeepAsync(intent.Id, now, cancellationToken);
                ApplicationDiagnostics.Metrics.OrleansIntentKept.Add(1);
                logger.LogCommandKept(intent.Id, intent.AttemptCount, intent.ConflictCount, intent.LastFailure ?? "none recorded");
                continue;
            }

            if (await KeptForIntegrityAsync(intent, now, cancellationToken))
            {
                continue;
            }

            claimable.Add(intent);
        }

        var claimed = claimable.Count == 0 ? [] : new HashSet<Guid>(await intents.ClaimAsync(claimable, claimedAt, cancellationToken));
        var resumed = 0;
        foreach (var intent in claimable.Where(intent => claimed.Contains(intent.Id)))
        {
            ApplicationDiagnostics.Metrics.OrleansIntentResumed.Add(1);
            logger.LogCommandResumed(intent.Id, intent.AttemptCount + 1);
            var payload = new AggregateCommandEnvelope(intent.Envelope.CommandTypeName, intent.Envelope.CommandJson, intent.Envelope.SessionContextJson, claimedAt);

            // Where it runs is taken from the signed envelope, not from the row beside it: the heavy claim is one of
            // the things the signature covers, and a row that disagrees with it was already refused above.
            var heavy = intent.Envelope.Heavy;
            try
            {
                var issued = await lane.SendAsync(AggregateSendLane.KeyOf(intent.Id, intent.AggregateId, heavy), Task.FromResult(payload), issue =>
                    handOver.HandOverAsync(intent.Id, issue, heavy, intent.AggregateId));
                IntentHandOver.Observe(issued, logger, intent.Id, intent.AggregateId, heavy);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await intents.RecordFailureAsync(intent.Id, IntentFailure.Describe(ex), cancellationToken);
            }

            resumed++;
        }

        return new ResumePass(resumed, due.Count >= batchSize);
    }

    /// <summary>
    /// Whether the command has reached a bound: its delivery bound — the record counts the hand-over of its dispatch as
    /// its first attempt — or its conflict bound, which it reaches once it has been resumed after a conflict as often as
    /// the bound allows, the count at which the bus moves a message that keeps conflicting to its dead-letter
    /// destination.
    /// </summary>
    internal bool IsExhausted(RecordedIntent intent) =>
        intent.AttemptCount >= _maxAttempts || intent.ConflictCount > _maxConflicts;

    /// <summary>
    /// Verifies the record under the host's integrity mode. A record that does not verify is kept at once under strict
    /// mode, with the reason recorded and no attempt counted, because resuming it again cannot change the answer; under
    /// permissive mode the failure is logged and the record is resumed. The row's own identity and heavy flag are held
    /// to the signed envelope before that, because they decide where the command runs; the aggregate the row names is
    /// not covered by the signature and decides only which activation the command is accepted into, not what it
    /// writes — the store's version check answers for that.
    /// </summary>
    /// <returns><see langword="true"/> when the record was kept.</returns>
    private async Task<bool> KeptForIntegrityAsync(RecordedIntent intent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Only the model's own records carry a routing beside the envelope; a command the bus outbox stored during a
        // rolling adoption carries a row id of its own and no routing, and is resumed by its envelope like any other.
        if (intent.RecordedByTheExecutionModel && (intent.Envelope.Id != intent.Id || intent.Envelope.Heavy != intent.Heavy))
        {
            if (_mode != BusEnvelopeIntegrityMode.Strict)
            {
                logger.LogIntentRoutingRefused(intent.Id, "resumed as the envelope says");
                return false;
            }

            await intents.RecordFailureAsync(intent.Id, RoutingReason, cancellationToken);
            await intents.KeepAsync(intent.Id, now, cancellationToken);
            ApplicationDiagnostics.Metrics.OrleansIntentKept.Add(1);
            logger.LogIntentRoutingRefused(intent.Id, "kept for an operator under strict integrity mode");
            return true;
        }

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, _mode, BusEnvelopeCanonical.Of(intent.Envelope), intent.Envelope.Signature, out var failure);
        var unsigned = failure == BusEnvelopeIntegrityFailure.Absent;
        switch (result)
        {
            case BusEnvelopeIntegrityResult.RejectedStrict:
                await intents.RecordFailureAsync(intent.Id, unsigned ? UnsignedReason : InvalidReason, cancellationToken);
                await intents.KeepAsync(intent.Id, now, cancellationToken);
                ApplicationDiagnostics.Metrics.OrleansIntentKept.Add(1);
                if (unsigned)
                {
                    logger.LogIntentUnsignedKept(intent.Id);
                }
                else
                {
                    logger.LogIntentIntegrityKept(intent.Id);
                }

                return true;
            case BusEnvelopeIntegrityResult.RejectedPermissive when unsigned:
                logger.LogIntentUnsignedResumed(intent.Id);
                return false;
            case BusEnvelopeIntegrityResult.RejectedPermissive:
                logger.LogIntentIntegrityResumed(intent.Id);
                return false;
            default:
                return false;
        }
    }
}

/// <summary>How a failure is recorded with a command, and whether it is a concurrency conflict.</summary>
internal static class IntentFailure
{
    public static string Describe(Exception exception) => $"{exception.GetType().FullName}: {exception.Message}";

    /// <summary>
    /// Whether the failure is a concurrency conflict as the bus transports classify one: the event store's
    /// <see cref="ConcurrencyException"/>. Anything else — a provider's own conflict included — is a failure there, and
    /// here.
    /// </summary>
    public static bool IsConflict(Exception exception) => exception is ConcurrencyException;
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
