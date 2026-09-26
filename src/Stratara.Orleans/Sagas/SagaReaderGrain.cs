using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Polly.Registry;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Diagnostics;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Projections;
using Stratara.Sagas.Abstractions;
using Stratara.Abstractions.Reflections;
using Stratara.Shared.Reflections;

namespace Stratara.Orleans.Sagas;

/// <summary>One grain per saga and partition, keyed <c>sagas:SagaName/partition</c>.</summary>
[Alias("Stratara.Orleans.ISagaReaderGrain")]
internal interface ISagaReaderGrain : IGrainWithStringKey
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
    /// Stops reading for <paramref name="pauser"/> until it resumes or <paramref name="lease"/> passes without a
    /// renewal; returns once no batch is in flight. Interleaves with a running catch-up, which stops at its next batch
    /// boundary, so a pause does not wait for a partition far behind.
    /// </summary>
    [AlwaysInterleave]
    [Alias("PauseHeldAsync")]
    Task PauseAsync(Guid pauser, TimeSpan lease);

    /// <summary>A pause for an anonymous pauser for ten minutes, or until <see cref="ResumeAsync()"/>; returns once no batch is in flight.</summary>
    [AlwaysInterleave]
    [Alias("PauseAsync")]
    Task PauseAsync();

    /// <summary>
    /// Releases <paramref name="pauser"/>'s pause and reads again, from whatever the checkpoint now says, once no pauser is
    /// left; a pauser not held changes nothing. Returns once the read is requested, not once it is done.
    /// </summary>
    [AlwaysInterleave]
    [Alias("ResumeHeldAsync")]
    Task ResumeAsync(Guid pauser);

    /// <summary>Releases the oldest anonymous pause; returns once the read is requested, not once it is done.</summary>
    [Alias("ResumeAsync")]
    Task ResumeAsync();

    /// <summary>Extends <paramref name="pauser"/>'s pause to <paramref name="lease"/> from now, and pauses again where the pause was lost.</summary>
    [AlwaysInterleave]
    [Alias("RenewPauseAsync")]
    Task RenewPauseAsync(Guid pauser, TimeSpan lease);
}

/// <summary>
/// The saga worker's job without the bus, for one saga: reads its partition in commit order from the saga's own
/// checkpoint and hands each entry the saga reacts to to that saga alone — a stateless saga through the framework's
/// saga handler, with the entry's events it declares a handler for; a stateful process to the process's grain. A saga
/// that fails on an entry stops its own reading of the partition, and every other saga goes on past the entry. Sagas
/// run in parallel with each other and in order within each, the <c>sagas</c> guarantees, unchanged; existing
/// stateless sagas run here without modification.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
[SagasRolePlacementFilter]
internal sealed class SagaReaderGrain(
    IServiceScopeFactory scopeFactory,
    IEventMapperFactory eventMapperFactory,
    IProjectionReplayState replayState,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<SagaGrainOptions> options,
    IOptions<CommitOrderOptions> commitOrder,
    Microsoft.Extensions.Logging.ILogger<SagaReaderGrain> logger)
    : StoreReaderGrain(scopeFactory, replayState, pipelineProvider, new StoreReaderSettings(options.Value.BatchSize, options.Value.PollInterval, options.Value.KeepAlivePeriod, commitOrder.Value.PartitionCount), logger),
        ISagaReaderGrain
{
    /// <summary>What every saga's consumer name starts with, so that no saga shares a checkpoint with a projection.</summary>
    public const string ConsumerPrefix = "sagas:";

    private Type? _sagaType;
    private EventRelevance? _relevance;

    /// <summary>The consumer a saga reads under: <see cref="ConsumerPrefix"/> and the name the saga handler gives it.</summary>
    public static string ConsumerOf(string sagaName) => ConsumerPrefix + sagaName;

    private string SagaName => Consumer[ConsumerPrefix.Length..];

    private SagaRegistrations Registrations => ServiceProvider.GetRequiredService<SagaRegistrations>();

    /// <summary>
    /// A reader whose saga this silo does not register — the saga was removed or renamed, or the silo runs a version
    /// without it — retires and deactivates, so that it neither stalls on every entry nor keeps its keep-alive, and a
    /// silo that registers the saga can host it.
    /// </summary>
    protected override StoreReaderRetirement RetiresAs =>
        Registrations.TypeOf(SagaName) is null ? StoreReaderRetirement.Unregistered : StoreReaderRetirement.None;

    protected override void LogRetirement() => logger.LogUnregisteredSagaReaderRetired(SagaName, Partition);

    /// <summary>
    /// One scope and one saga instance per session run, resolved after the run's session is set, so a saga's
    /// dependency that takes its tenant when it is constructed takes the entry's; per entry, the recorded session. A
    /// fact a process handles travels to its grain with the session it was recorded under. Once the grain knows its saga's
    /// relevance — the event types a stateless saga declares, or any resolvable type for a process — the mapper leaves an
    /// entry outside it unread, and no scope is built for it.
    /// </summary>
    protected override async Task<int> ApplyBatchAsync(CommittedBatch batch, CancellationToken cancellationToken)
    {
        await using var runs = new SessionRuns<SagaReaderRun>(ScopeFactory, services =>
        {
            var handler = services.GetRequiredService<ISagaHandler>();
            var run = new SagaReaderRun(handler, ResolveSaga(services), GrainFactory);
            _relevance ??= run.Relevance;
            return run;
        });

        return await Loop.ApplyEachAsync(batch, async (entry, entryToken) =>
        {
            IReadOnlyList<IEvent>? events = null;
            if (_relevance is { } known)
            {
                events = await eventMapperFactory.MapToEventsAsync([entry], known, entryToken);
                if (events.Count == 0)
                {
                    return;
                }
            }

            var run = await runs.EnterAsync(entry);
            events ??= await eventMapperFactory.MapToEventsAsync([entry], run.Relevance, entryToken);
            if (events.Count > 0)
            {
                await run.ApplyAsync(entry, events, entryToken);
            }
        }, cancellationToken);
    }

    /// <summary>A saga without a checkpoint starts where the host's sagas read; see <see cref="SagaStart"/>.</summary>
    protected override Task StartAsync(IProjectionCheckpointStore checkpoints, string reader, CancellationToken cancellationToken) =>
        SagaStart.EnsureAsync(checkpoints, reader, Partition, Consumer, Registrations.Consumers, cancellationToken);

    /// <summary>
    /// Resolves the saga of the grain's name, whose type the registrations name; the container builds it, so a factory
    /// registration or a lifetime the host chose holds for every batch.
    /// </summary>
    /// <exception cref="InvalidOperationException">The saga is no longer registered on this silo.</exception>
    private ISaga ResolveSaga(IServiceProvider services)
    {
        var type = _sagaType ??= Registrations.TypeOf(SagaName)
                                 ?? throw new InvalidOperationException($"No saga named '{SagaName}' is registered on this silo.");
        return services.GetService(type) as ISaga
               ?? services.GetServices<ISaga>().FirstOrDefault(s => s.GetType() == type)
               ?? throw new InvalidOperationException($"The saga {type.FullName} named '{SagaName}' is no longer registered on this silo.");
    }
}

/// <summary>
/// What a saga reader applies the entries of one session run with: the saga and the handler that drives it. A
/// stateless saga receives the entry's events it declares a handler for, and nothing where the entry has none; a
/// process's facts go to the process's grain.
/// </summary>
internal sealed class SagaReaderRun(ISagaHandler handler, ISaga saga, IGrainFactory grainFactory)
{
    private readonly HashSet<string> _relevant = new(handler.GetRelevantEventTypeNames(saga), StringComparer.Ordinal);

    /// <summary>Whether the saga is a stateful process, whose facts are decided by the process rather than by the event types it declares.</summary>
    public bool IsProcess => saga is ISagaProcess;

    /// <summary>
    /// Which entries the saga has a use for: the event types a stateless saga declares a handler for, or any type that
    /// resolves for a process, which decides from the event itself — an unresolvable entry named like a type the process
    /// declares a handler for is read too, so it fails as an unregistered type does. Built on first use.
    /// </summary>
    public EventRelevance Relevance => _relevance ??= saga is ISagaProcess
        ? EventRelevance.AnyResolvableWith(handler.GetRelevantEventTypes(saga))
        : EventRelevance.ForTypes(handler.GetRelevantEventTypes(saga));

    private EventRelevance? _relevance;

    public async Task ApplyAsync(EventStreamEntry entry, IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
    {
        var relevantEvents = events.Where(e => _relevant.Contains(e.EventTypeName)).ToList();
        if (relevantEvents.Count > 0)
        {
            await handler.HandleAsync(saga, relevantEvents, cancellationToken);
        }

        if (saga is not ISagaProcess process)
        {
            return;
        }

        foreach (var @event in events.Where(process.Handles))
        {
            var key = SagaProcessKey.Of(process.GetType().Name, process.CorrelationOf(@event));
            await grainFactory.GetGrain<ISagaProcessGrain>(key).HandleAsync(entry.StreamId, entry.Version, RecordedSession.KeyOf(entry), cancellationToken);
        }
    }
}

/// <summary>
/// Where a saga without a checkpoint in a partition starts: where the host's sagas already read there, so that a saga
/// added to a running deployment — or every saga on the first start after the shared checkpoint of a release before
/// 4.2.0 — runs no side effect for history the host's sagas already read.
/// </summary>
internal static class SagaStart
{
    /// <summary>
    /// Gives every saga of the host that has no checkpoint in the partition one: the furthest of the shared checkpoint
    /// a release before 4.2.0 left and the checkpoints the host's sagas hold there, or the beginning where there is
    /// none. Each reader runs this before it first reads, for all of the host's sagas and not for its own alone, so a
    /// saga whose reader starts a moment later than another's, once that one has read on, starts where the other
    /// started and not where it has got to. A checkpoint is only ever created, never replaced, so two readers starting
    /// together agree on it, and one at the beginning is kept as one — a saga still at the beginning is a saga that
    /// has not applied its first entry, not one without a checkpoint.
    /// </summary>
    /// <param name="checkpoints">The checkpoint store.</param>
    /// <param name="reader">The name of the reader the host reads under.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="consumer">The consumer of the saga whose reader starts.</param>
    /// <param name="hostConsumers">The consumer of every saga the host registers.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <exception cref="NotSupportedException">The checkpoint store does not tell a missing checkpoint from one at the beginning.</exception>
    public static async Task EnsureAsync(
        IProjectionCheckpointStore checkpoints,
        string reader,
        int partition,
        string consumer,
        IReadOnlyList<string> hostConsumers,
        CancellationToken cancellationToken)
    {
        List<string> consumers = [consumer, .. hostConsumers.Where(other => !string.Equals(other, consumer, StringComparison.Ordinal))];
        var held = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var candidate in consumers)
        {
            held[candidate] = await checkpoints.FindAsync(candidate, partition, reader, cancellationToken);
        }

        var missing = consumers.Where(candidate => held[candidate] is null).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var position = StartingPosition(await checkpoints.FindAsync(SagaGrain.ConsumerName, partition, reader, cancellationToken), held.Values);
        foreach (var starting in missing)
        {
            await checkpoints.CreateAsync(starting, partition, reader, position, cancellationToken);
        }
    }

    /// <summary>
    /// Where a saga without a checkpoint starts: the furthest of the checkpoint the sagas shared before 4.2.0 and the
    /// checkpoints the host's sagas hold, or the beginning where there is none.
    /// </summary>
    public static long StartingPosition(long? shared, IEnumerable<long?> held) =>
        held.Aggregate(shared ?? 0, (furthest, position) => Math.Max(furthest, position ?? 0));
}

/// <summary>Every registered saga's reader for the partition, named from the registrations without building a saga.</summary>
internal sealed class SagaNudgeTarget(SagaRegistrations registrations, StoreReaderLease lease) : INudgeTarget
{
    private readonly IReadOnlyList<string> _names = registrations.Consumers;

    public IReadOnlyList<string> ConsumerNames => _names;

    public IReadOnlyList<string> SupersededConsumerNames { get; } = [SagaGrain.ConsumerName];

    /// <summary>
    /// Wakes every saga's reader of the partition. A wake-up that cannot even be sent is collected rather than thrown at
    /// once, so one saga the call fails for does not cost the others behind it their wake-up; the dispatcher logs what
    /// failed.
    /// </summary>
    /// <exception cref="AggregateException">A wake-up could not be sent for one or more sagas.</exception>
    public Task NudgeAsync(IGrainFactory grainFactory, int partition)
    {
        List<Exception>? failures = null;
        foreach (var name in _names)
        {
            try
            {
                grainFactory.GetGrain<ISagaReaderGrain>(StoreReaderGrainKey.Of(name, partition)).NudgeAsync().Ignore();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures is null ? Task.CompletedTask : Task.FromException(new AggregateException(failures));
    }

    public async Task EnsureRunningAsync(IGrainFactory grainFactory, int partition)
    {
        foreach (var name in _names)
        {
            await grainFactory.GetGrain<ISagaReaderGrain>(StoreReaderGrainKey.Of(name, partition)).EnsureRunningAsync();
        }
    }

    public ValueTask<StoreReaderHold> PauseAsync(IGrainFactory grainFactory, int partition) =>
        StoreReaderPause.PauseAllAsync([.. _names.Select(name => Reader(grainFactory, name, partition))], lease);

    private static PausedReader Reader(IGrainFactory grainFactory, string name, int partition)
    {
        var key = StoreReaderGrainKey.Of(name, partition);
        var grain = grainFactory.GetGrain<ISagaReaderGrain>(key);
        return new PausedReader(key, grain.PauseAsync, grain.RenewPauseAsync, grain.ResumeAsync);
    }
}
