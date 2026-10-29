using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Polly.Registry;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.Diagnostics;
using Stratara.Orleans.Hosting;
using Stratara.Resilience;

namespace Stratara.Orleans.Projections;

/// <summary>How a store-reading grain reads: the batch it asks for, its poll, its keep-alive and the partitions the host has.</summary>
/// <param name="BatchSize">How many entries one read from the store returns.</param>
/// <param name="PollInterval">How often the grain reads the store without having been nudged.</param>
/// <param name="KeepAlivePeriod">How often the cluster brings a lost grain back.</param>
/// <param name="PartitionCount">How many partitions the host reads; a grain of a partition at or beyond it retires.</param>
/// <param name="IdleCheckpointInterval">How long the position past batches that applied nothing may go unwritten; zero writes it after every batch.</param>
internal sealed record StoreReaderSettings(int BatchSize, TimeSpan PollInterval, TimeSpan KeepAlivePeriod, int PartitionCount, TimeSpan IdleCheckpointInterval = default);

/// <summary>Why a store-reading grain retires when it is activated, if it does.</summary>
internal enum StoreReaderRetirement
{
    /// <summary>The grain reads.</summary>
    None,

    /// <summary>The host reads what the grain read under other names now; the grain answers calls until it is collected.</summary>
    Superseded,

    /// <summary>
    /// The grain's consumer is not registered on this silo; the grain deactivates once it has answered the call that
    /// activated it, refuses a pause and answers a wait as not reached.
    /// </summary>
    Unregistered,

    /// <summary>The grain's partition is beyond the host's partition count; the grain decides this itself, a derived grain never does.</summary>
    BeyondPartitionCount,
}

/// <summary>
/// What every store-reading grain does, whatever it applies: parse its key into consumer and
/// partition, run one catch-up loop at a time over its partition, read when nudged, when its poll
/// fires and when its keep-alive reminder arrives, and wait for a running loop before it deactivates
/// so a successor never applies beside it. None of the three waits for the loop it requests: the loop, not the
/// grain's turn, keeps one catch-up per partition, and a poll or a keep-alive that held the turn for a catch-up longer
/// than the response timeout would leave the next tick undelivered. A derived grain lets the keep-alive tick interleave
/// through <see cref="KeepAliveInterleaving"/>, so it waits for no other call either. A grain of a partition the host no
/// longer has — its count was lowered — retires when it is activated: it unregisters its keep-alive, logs that it did,
/// reads nothing and deactivates once it has answered the call that activated it, and every call it still receives does
/// nothing; so does a grain a derived grain says is superseded or unregistered. A
/// derived grain says what applying a batch means, when reading is suspended beyond the pauses and the replay this one
/// already counts, and where a consumer without a checkpoint starts.
/// </summary>
internal abstract class StoreReaderGrain(
    IServiceScopeFactory scopeFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    StoreReaderSettings settings,
    ILogger logger) : Grain, IRemindable
{
    private const string KeepAliveReminder = "keep-alive";

    /// <summary>How long a pause taken through the parameterless methods lasts: its caller, a silo of an older version, cannot renew it.</summary>
    internal static readonly TimeSpan AnonymousLease = TimeSpan.FromMinutes(10);

    private readonly CancellationTokenSource _stopping = new();
    private readonly List<StoreReaderWaiter> _waiters = [];
    private TimeProvider _time = TimeProvider.System;
    private StoreReaderLoop? _loop;
    private PartitionHeads? _heads;
    private IGrainTimer? _poll;
    private StoreReaderPausers? _pausers;
    private StoreReaderRetirement _retirement;

    /// <summary>The consumer this grain reads for, from its key.</summary>
    protected string Consumer { get; private set; } = string.Empty;

    /// <summary>The partition this grain reads, from its key.</summary>
    protected int Partition { get; private set; }

    /// <summary>Where each batch resolves its services.</summary>
    protected IServiceScopeFactory ScopeFactory => scopeFactory;

    /// <summary>The grain's catch-up loop.</summary>
    /// <exception cref="InvalidOperationException">The grain has not been activated.</exception>
    protected StoreReaderLoop Loop => _loop ?? throw new InvalidOperationException("The grain has not been activated.");

    /// <summary>Whether the grain's partition is beyond the host's partition count, or its reader was superseded or is not registered, so the grain reads nothing.</summary>
    private bool Retired => _retirement != StoreReaderRetirement.None;

    /// <summary>Whether reading stops at the next batch boundary; a replay suspends every reader, and so does a pauser.</summary>
    protected virtual bool Suspended => Pausers.Any || replayState.IsReplayActive;

    /// <summary>Who holds the grain paused.</summary>
    /// <exception cref="InvalidOperationException">The grain has not been activated.</exception>
    private StoreReaderPausers Pausers => _pausers ?? throw new InvalidOperationException("The grain has not been activated.");

    /// <summary>
    /// Pauses for <paramref name="pauser"/> until <paramref name="lease"/> has passed without a renewal, waits for a
    /// running loop to end, and forgets the cached position: whoever pauses is about to change the checkpoint behind
    /// the grain's back. Pausers are held apart, so two callers that overlap hold the reader until the last of them
    /// resumes or lapses. The lease starts again once the running loop has ended, so a long batch does not eat it — and
    /// a pause that lapsed during that wait holds again. A pause of a pauser released within the last lease — delivered
    /// after its own resume — does nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The grain's consumer is not registered on this silo, so it cannot be held paused here.</exception>
    public async Task PauseAsync(Guid pauser, TimeSpan lease)
    {
        if (AnswersAsRetired())
        {
            RefusePauseWhereUnregistered();
            return;
        }

        if (!Pausers.Hold(pauser, lease))
        {
            return;
        }

        await Loop.WaitForRunningAsync(CancellationToken.None);
        Pausers.Hold(pauser, lease);
        Loop.Invalidate();
    }

    /// <summary>A pause of an older silo, which cannot renew it: held for an anonymous pauser for <see cref="AnonymousLease"/>.</summary>
    /// <exception cref="InvalidOperationException">The grain's consumer is not registered on this silo, so it cannot be held paused here.</exception>
    public async Task PauseAsync()
    {
        if (AnswersAsRetired())
        {
            RefusePauseWhereUnregistered();
            return;
        }

        Pausers.HoldAnonymously(AnonymousLease);
        await Loop.WaitForRunningAsync(CancellationToken.None);
        Loop.Invalidate();
    }

    /// <summary>
    /// Releases <paramref name="pauser"/>'s pause and starts the reader again where it was the last. A pauser the grain
    /// does not hold — a resume delivered twice, a pause that lapsed, an activation that forgot it — releases nothing,
    /// but the grain still forgets its cached position: the pauser changed the checkpoint, and a grain that read while
    /// it should have been paused holds a position the checkpoint no longer has.
    /// </summary>
    public Task ResumeAsync(Guid pauser)
    {
        if (AnswersAsRetired())
        {
            return Task.CompletedTask;
        }

        if (Pausers.Release(pauser))
        {
            return RestartAsync();
        }

        Loop.Invalidate();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A resume of an older silo: releases the oldest anonymous pause, and starts the reader again where it was the last;
    /// where it releases nothing, the grain forgets its cached position as a held resume does.
    /// </summary>
    public Task ResumeAsync()
    {
        if (AnswersAsRetired())
        {
            return Task.CompletedTask;
        }

        if (Pausers.ReleaseOldestAnonymous())
        {
            return RestartAsync();
        }

        Loop.Invalidate();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Extends <paramref name="pauser"/>'s pause to <paramref name="lease"/> from now. A pauser the grain no longer holds —
    /// its pause lapsed, or the grain was activated again elsewhere and forgot it — pauses the grain again, without
    /// waiting for a running loop, which stops at its next batch boundary. A renewal of a pauser released within the last
    /// lease — delivered after its resume — does nothing.
    /// </summary>
    public Task RenewPauseAsync(Guid pauser, TimeSpan lease)
    {
        if (AnswersAsRetired())
        {
            return Task.CompletedTask;
        }

        var held = Pausers.Holds(pauser);
        if (Pausers.Hold(pauser, lease) && !held)
        {
            Loop.Invalidate();
        }

        return Task.CompletedTask;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var (consumer, partition) = StoreReaderGrainKey.Parse(this.GetPrimaryKeyString());
        Consumer = consumer;
        Partition = partition;
        _time = ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        _pausers = new StoreReaderPausers(_time);
        if (partition >= settings.PartitionCount)
        {
            await RetireAsync(StoreReaderRetirement.BeyondPartitionCount);
            logger.LogStoreReaderRetired(Consumer, Partition, settings.PartitionCount);
            await base.OnActivateAsync(cancellationToken);
            return;
        }

        if (RetiresAs is var retirement and not StoreReaderRetirement.None)
        {
            await RetireAsync(retirement);
            LogRetirement();
            await base.OnActivateAsync(cancellationToken);
            return;
        }

        _loop = new StoreReaderLoop(scopeFactory, pipelineProvider.GetPipeline(ResilienceNames.PrecedingFact), consumer, partition, settings.BatchSize, logger, StartAsync)
        {
            PositionChanged = AnswerWaiters,
            IdleCheckpointInterval = settings.IdleCheckpointInterval,
        };
        _heads = ServiceProvider.GetService<PartitionHeads>();
        _poll ??= this.RegisterGrainTimer(
            _ => RequestPoll(),
            new GrainTimerCreationOptions
            {
                DueTime = settings.PollInterval,
                Period = settings.PollInterval,
                Interleave = false,
                KeepAlive = true,
            });
        logger.LogStoreReaderStarted(consumer, partition);
        await base.OnActivateAsync(cancellationToken);
    }

    public Task EnsureRunningAsync() => AnswersAsRetired() ? Task.CompletedTask : this.RegisterOrUpdateReminder(KeepAliveReminder, settings.KeepAlivePeriod, settings.KeepAlivePeriod);

    /// <summary>
    /// Requests a catch-up without waiting for it. A catch-up that fails is logged and counted by the loop itself,
    /// whichever wake-up or poll started it, and the next one reads again.
    /// </summary>
    public Task NudgeAsync()
    {
        if (AnswersAsRetired())
        {
            return Task.CompletedTask;
        }

        RequestCatchUp().Ignore();
        return Task.CompletedTask;
    }

    public Task<int> CatchUpAsync() => AnswersAsRetired() ? Task.FromResult(0) : RequestCatchUp();

    public Task<long> PositionAsync() => AnswersAsRetired() ? Task.FromResult(0L) : Loop.PositionAsync();

    /// <summary>
    /// Answers once the reader has applied up to <paramref name="position"/> and is not suspended, or once
    /// <paramref name="budget"/> has passed, with how the reader stood then. Requests a catch-up, so the reader reads now
    /// rather than at its next poll. A suspended reader never answers that it reached the position, whatever its
    /// checkpoint says: a pauser is about to change the checkpoint, and a reader that read while it should have been
    /// paused may hold a position over a read model about to be emptied. A waiting call holds the grain's deactivation
    /// until it is answered, so callers keep the budget short; a waiter still registered when the deactivation runs is
    /// answered with how the reader stood, and the caller asks the next activation.
    /// </summary>
    public async Task<StoreReaderProgress> WaitForAsync(long position, TimeSpan budget)
    {
        if (AnswersAsRetired())
        {
            if (_retirement != StoreReaderRetirement.Unregistered)
            {
                return new StoreReaderProgress(false, StoreReaderState.Retired);
            }

            await Task.Delay(budget, _time);
            return new StoreReaderProgress(false, StoreReaderState.Unregistered);
        }

        if (!HasReached(position))
        {
            var waiter = new StoreReaderWaiter(position);
            _waiters.Add(waiter);
            try
            {
                using var expiry = new CancellationTokenSource(budget, _time);
                await using var registration = expiry.Token.Register(() => waiter.Completion.TrySetResult());
                RequestCatchUp().Ignore();
                await waiter.Completion.Task;
            }
            finally
            {
                _waiters.Remove(waiter);
            }
        }

        return new StoreReaderProgress(HasReached(position), State);
    }

    /// <summary>Requests a poll and answers at once, whatever the reader is doing; see <see cref="RequestPoll"/>.</summary>
    Task IRemindable.ReceiveReminder(string reminderName, TickStatus status) => AnswersAsRetired() ? Task.CompletedTask : RequestPoll();

    /// <summary>
    /// A loop a nudge started holds no request; the activation waits for it so a successor never applies beside it.
    /// A loop that outlasts the deactivation's budget is cancelled — it stops at its next store call or batch
    /// boundary — and waited for once more; what the grain reported is withdrawn however the wait ended. A position
    /// the loop held back past entries that applied nothing is written then, unless reading is suspended, when whoever
    /// suspended it may be changing the checkpoint.
    /// </summary>
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (Retired)
        {
            _stopping.Dispose();
            await base.OnDeactivateAsync(reason, cancellationToken);
            return;
        }

        foreach (var waiter in _waiters)
        {
            waiter.Completion.TrySetResult();
        }

        try
        {
            await Loop.WaitForRunningAsync(cancellationToken);
        }
        finally
        {
            if (Loop.Running is not null)
            {
                await _stopping.CancelAsync();
                await Loop.WaitForRunningAsync(CancellationToken.None);
            }

            if (!Suspended)
            {
                await Loop.FlushDeferredAsync();
            }

            Loop.Withdraw();
            _stopping.Dispose();
            logger.LogStoreReaderStopped(Consumer, Partition);
        }

        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    private async Task RetireAsync(StoreReaderRetirement retirement)
    {
        _retirement = retirement;
        if (await this.GetReminder(KeepAliveReminder) is { } keepAlive)
        {
            await this.UnregisterReminder(keepAlive);
        }
    }

    /// <summary>
    /// Whether the grain retired, so the call does nothing. A retired grain that deactivates does so once the call is
    /// answered, not while it is activated: an activation that deactivates before it has answered the call that
    /// activated it has the runtime forward the call to a new activation, which retires again, until the runtime
    /// rejects the call — for a keep-alive tick, an undelivered reminder logged at Error.
    /// </summary>
    private bool AnswersAsRetired()
    {
        if (_retirement is StoreReaderRetirement.Unregistered or StoreReaderRetirement.BeyondPartitionCount)
        {
            this.DeactivateOnIdle();
        }

        return Retired;
    }

    /// <summary>
    /// A reader whose consumer this silo does not register refuses a pause rather than pretend to hold it: the next
    /// activation, on a silo that registers the consumer, would read while the pauser believes the partition stopped.
    /// The pauser fails naming the reader and releases the readers it did pause.
    /// </summary>
    /// <exception cref="InvalidOperationException">The grain's consumer is not registered on this silo.</exception>
    private void RefusePauseWhereUnregistered()
    {
        if (_retirement == StoreReaderRetirement.Unregistered)
        {
            throw new InvalidOperationException(
                $"The reader of '{Consumer}' on partition {Partition} answered from a silo that does not register '{Consumer}', so it cannot be held paused; pause again once every silo registers it.");
        }
    }

    /// <summary>
    /// Whether the grain's key names a reader the host does not run, so the grain retires when it is activated, as one
    /// beyond the partition count does; the default is <see cref="StoreReaderRetirement.None"/>. A superseded reader is
    /// left to the runtime's idle collection rather than deactivated at once: a silo of an earlier release still calls
    /// it in a rolling cluster, and the call that activated it is answered, doing nothing, instead of being forwarded to
    /// an activation that retires again until the runtime rejects it. An unregistered reader deactivates once it has
    /// answered that call, so that a silo that registers its consumer can host it.
    /// </summary>
    protected virtual StoreReaderRetirement RetiresAs => StoreReaderRetirement.None;

    /// <summary>Says why the grain retired; called once it has unregistered its keep-alive.</summary>
    protected virtual void LogRetirement()
    {
    }

    /// <summary>
    /// Gives the consumer its starting position in the partition where it has no checkpoint there: runs once per
    /// activation, before the grain first reads its checkpoint, and again on the next catch-up where it failed. The
    /// default gives none, so a consumer without a checkpoint reads from position <c>0</c>, the beginning of the store.
    /// A grain that starts elsewhere writes the position as the consumer's checkpoint here, under
    /// <paramref name="reader"/>, so a later activation finds it and does not ask again.
    /// </summary>
    /// <param name="checkpoints">The checkpoint store of the catch-up.</param>
    /// <param name="reader">The name of the reader the host reads under.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>A task that completes once the consumer has its starting position.</returns>
    protected virtual Task StartAsync(IProjectionCheckpointStore checkpoints, string reader, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Applies a batch in order and returns the index of the first entry that did not apply, and how many applied entries handed anything to a handler.</summary>
    protected abstract Task<BatchOutcome> ApplyBatchAsync(CommittedBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Lets every pause whose lease has passed lapse before reading, so a pauser that died leaves the reader paused for
    /// no longer than its lease and a poll; the last pause to lapse forgets the cached position, as a resume does. Then
    /// starts a catch-up without waiting for it where none is running; a loop that fails is logged and counted by the
    /// loop itself. A running loop is left alone: it reads until the store has nothing newer, a commit's nudge makes it
    /// read once more, and a poll that did too would keep a loop stalled on an entry retrying it back to back, never
    /// ending, instead of once per poll as before. See <see cref="PollAsync"/> for when a poll reads.
    /// </summary>
    private Task RequestPoll()
    {
        if (Pausers.Lapse(logger, Consumer, Partition))
        {
            Loop.Invalidate();
        }

        if (Loop.Running is null)
        {
            PollAsync().Ignore();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads only where there may be something to read: a reader that knows its position, is not suspended and holds
    /// back no checkpoint whose interval has passed asks the silo's <see cref="PartitionHeads"/> first, and reads nothing
    /// where its partition holds nothing past it. Every other poll reads as before — a suspended reader so that it
    /// forgets its position, one that does not know its position so that it learns it.
    /// </summary>
    private async Task PollAsync()
    {
        if (_heads is { } heads
            && !Suspended
            && !Loop.DeferralDue
            && Loop.TryGetPosition(out var position)
            && !await heads.HasAnythingPastAsync(Partition, position, settings.PollInterval / 2))
        {
            return;
        }

        if (Loop.Running is null)
        {
            RequestCatchUp().Ignore();
        }
    }

    /// <summary>How the reader stands: suspended before stalled on an entry before stalled on a read.</summary>
    private StoreReaderState State
    {
        get
        {
            if (Suspended)
            {
                return StoreReaderState.Suspended;
            }

            if (Loop.StalledOnEntry)
            {
                return StoreReaderState.StalledOnEntry;
            }

            return Loop.StalledOnRead ? StoreReaderState.StalledOnRead : StoreReaderState.Advancing;
        }
    }

    private bool HasReached(long position) => !Suspended && Loop.TryGetPosition(out var current) && current >= position;

    private void AnswerWaiters()
    {
        foreach (var waiter in _waiters.Where(waiter => HasReached(waiter.Position)))
        {
            waiter.Completion.TrySetResult();
        }
    }

    private Task RestartAsync()
    {
        Loop.Invalidate();
        return NudgeAsync();
    }

    /// <summary>Every loop, whoever requested it, runs under the activation's lifetime, so a deactivation can stop it.</summary>
    private Task<int> RequestCatchUp() => Loop.RequestCatchUp(ApplyBatchAsync, () => Suspended, _stopping.Token);
}
