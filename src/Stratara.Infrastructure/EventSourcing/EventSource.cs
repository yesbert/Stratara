using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Diagnostics;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.EventSourcing.Mapping;
using Stratara.Shared.Partitioning;
using Stratara.Shared.Reflections;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Default in-process <see cref="IEventSource"/> that buffers events per <c>SaveChanges</c> batch,
/// resolves the data-owner Subject for each event, persists them through the
/// <see cref="IWriteUnitOfWork"/>, and dispatches the resulting <c>EventBundle</c> to the outbox.
/// </summary>
/// <remarks>
/// <para>
/// Subject (TenantId / UserId) is resolved in the following priority order: explicit override
/// (via <see cref="AppendOnBehalfOfAsync{TAggregate}"/>), per-batch cache, the owner recorded on the
/// stream, <see cref="IAggregateCreationEvent"/> payload, then <see cref="SessionContext"/> fallback.
/// If none yields a non-empty Subject, the append fails fast with an <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// Concurrency conflicts — a unique-index violation on the stream version, recognised by the
/// <see cref="IStoreConflictDetector"/> the store registration contributes for its provider — are
/// surfaced as <see cref="ConcurrencyException"/> and recorded in
/// <c>ApplicationDiagnostics.Metrics.EventSourceAppendConflicts</c>. With no detector registered
/// only EF Core's own concurrency exception is recognised; a provider's unique violation then
/// propagates as the persistence failure it was.
/// </para>
/// </remarks>
internal sealed partial class EventSource(
    ISnapshotService snapshotService,
    IWriteUnitOfWork unitOfWork,
    ISessionContextProvider sessionContextProvider,
    IEventBundleOutboxDispatcher outboxDispatcher,
    ISecureJsonSerializer serializer,
    IEnumerable<IStoreConflictDetector> conflictDetectors,
    IBusEnvelopeSigner? signer = null,
    ILogger<EventSource>? logger = null) : IEventSource
{
    private readonly List<EventStreamEntry> _eventStreamEntries = [];
    private readonly Dictionary<Guid, long> _streamVersions = new();

    /// <remarks>
    /// Per-batch Subject cache. Once a stream's Subject is resolved (via aggregate lookup or creation
    /// event), subsequent events in the same SaveChanges call reuse it without re-querying the database.
    /// </remarks>
    private readonly Dictionary<Guid, EventSubject> _streamSubjects = new();

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);

        return await eventStreamRepository.StreamExistsAsync(streamId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<long> GetCurrentVersionAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);
        return await eventStreamRepository.GetVersionOrDefaultAsync(streamId, cancellationToken);
    }

    /// <inheritdoc/>
    public Task CreateAsync<TAggregate>(Guid streamId, object @event, CancellationToken cancellationToken = default)
        where TAggregate : notnull, new() => CreateRangeAsync<TAggregate>(streamId, [@event], cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown when a stream with the given <paramref name="streamId"/> already exists.</exception>
    public async Task CreateRangeAsync<TAggregate>(Guid streamId, IEnumerable<object> events,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new()
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);

        if (await eventStreamRepository.StreamExistsAsync(streamId, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Stream with ID {streamId} already exists. Use AppendToStream to add events.");
        }

        _streamVersions[streamId] = 0;
        await AddEventsToStreamAsync<TAggregate>(streamId, events, statedSubject: null, cancellationToken);
    }

    /// <inheritdoc/>
    public Task AppendAsync<TAggregate>(Guid streamId, object @event, CancellationToken cancellationToken = default)
        where TAggregate : notnull, new() => AppendRangeAsync<TAggregate>(streamId, [@event], cancellationToken);

    /// <inheritdoc/>
    public Task AppendRangeAsync<TAggregate>(Guid streamId, IEnumerable<object> events,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new() =>
        AppendRangeCoreAsync<TAggregate>(streamId, events, statedSubject: null, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="subject"/> names no tenant. A caller that states the Subject has
    /// also stated that no other candidate applies, so the append fails instead of falling back.
    /// </exception>
    public Task AppendOnBehalfOfAsync<TAggregate>(Guid streamId, object @event, EventSubject subject,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new()
    {
        if (subject.TenantId == Guid.Empty)
        {
            throw new ArgumentException(
                $"Explicit Subject for event {@event.GetType().Name} on stream {streamId} names no tenant. " +
                "Supply a Subject with a tenant id, or use AppendAsync to let the Subject be resolved.",
                nameof(subject));
        }

        return AppendRangeCoreAsync<TAggregate>(streamId, [@event], subject, cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ConcurrencyException">
    /// Thrown when an optimistic-concurrency conflict (duplicate stream-version) is detected while
    /// persisting the buffered events.
    /// </exception>
    /// <exception cref="SessionRequiredException">Thrown when no <see cref="SessionContext"/> is set on the current scope.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an event was appended under a session carrying no causation id — the store requires
    /// one of every entry, and the command-audit pipeline is what supplies it. A host that has not
    /// registered it is misconfigured, which is not the same as a caller without an identity, so this
    /// is not a <see cref="SessionRequiredException"/>. Thrown before anything is written.
    /// </exception>
    /// <remarks>
    /// A save that fails for any reason discards the staged batch, as a successful one clears it: a
    /// handler that runs again in the same scope then starts from what it appends anew, rather than
    /// from a batch it has already staged once.
    /// </remarks>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await PersistAndPublishAsync(cancellationToken);
        }
        finally
        {
            ClearBatchState();
        }
    }

    private async Task PersistAndPublishAsync(CancellationToken cancellationToken)
    {
        var eventBundle = PrepareEventBundle();
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);

        await eventStreamRepository.AddRangeAsync(_eventStreamEntries, cancellationToken);
        if (outboxDispatcher.StoresBundlesWithCommit)
        {
            await outboxDispatcher.StoreEventBundleAsync(eventBundle, transaction, cancellationToken);
        }

        try
        {
            await transaction.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (IsConcurrencyOrUniqueViolation(ex))
        {
            var firstEntry = _eventStreamEntries.FirstOrDefault();
            var streamId = firstEntry?.StreamId ?? Guid.Empty;
            var aggregateTypeName = firstEntry?.AggregateTypeName ?? string.Empty;
            var bucketId = firstEntry?.BucketId ?? 0;
            ApplicationDiagnostics.Metrics.EventSourceAppendConflicts.Add(1,
                new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.AggregateType, ApplicationDiagnostics.MetricTags.TypeNameValue(aggregateTypeName)),
                new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.BucketId, bucketId));
            throw new ConcurrencyException(streamId, aggregateTypeName, ex);
        }

        await outboxDispatcher.EnqueueEventBundleAsync(eventBundle, cancellationToken);
        await SnapshotCommittedAsync(cancellationToken);
    }

    /// <summary>
    /// A snapshot is a cache of committed state. It is taken only once the events are committed and
    /// published, so a save that fails leaves none behind, and a failure to take it — a cancellation
    /// included — is logged rather than failing a save whose events are recorded.
    /// </summary>
    private async Task SnapshotCommittedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await snapshotService.AddSnapshotIfNeededAsync(_eventStreamEntries, cancellationToken);
        }
        catch (Exception ex)
        {
            LogSnapshotFailed(logger ?? NullLogger<EventSource>.Instance, ex, _eventStreamEntries.Count);
        }
    }

    [LoggerMessage(
        EventId = LogEvents.EventStore.SnapshotFailed,
        Level = LogLevel.Warning,
        Message = "Writing a snapshot after {EventCount} committed event(s) failed. The events are recorded and published; a later save writes a snapshot.")]
    private static partial void LogSnapshotFailed(ILogger logger, Exception exception, int eventCount);

    private void ClearBatchState()
    {
        _eventStreamEntries.Clear();
        _streamVersions.Clear();
        _streamSubjects.Clear();
    }

    /// <summary>
    /// A conflict the persistence layer names as one, or a unique violation a registered detector
    /// recognises. Every detector sees the exception as the unit of work threw it, whatever its
    /// type: which layer wraps the provider's exception is a provider detail, and a unit of work that
    /// is not Entity Framework's surfaces the provider's exception unwrapped.
    /// </summary>
    private bool IsConcurrencyOrUniqueViolation(Exception ex) =>
        ex is ConcurrencyConflictException or DbUpdateConcurrencyException
        || conflictDetectors.Any(detector => detector.IsUniqueViolation(ex));

    /// <summary>
    /// Maps and signs the bundle before the transaction opens, so a save with no session fails
    /// before anything is committed, and so the same instance can be stored with the commit and
    /// published after it.
    /// </summary>
    private EventBundle PrepareEventBundle()
    {
        var sessionContext = sessionContextProvider.Current ?? throw new SessionRequiredException("Session context is not set");
        if (_eventStreamEntries.Find(entry => string.IsNullOrWhiteSpace(entry.CausationId)) is { } uncaused)
        {
            throw new InvalidOperationException(
                $"Event {uncaused.EventTypeName} on stream {uncaused.StreamId} was appended under a session that " +
                "carries no causation id, which the store requires of every entry. Register the command-audit " +
                "pipeline with AddCommandAuditing() so a dispatched command supplies one, or set a causation id on " +
                "the session for work that no command started.");
        }

        var eventBundle = _eventStreamEntries.MapToEventBundle(sessionContext);
        return signer is null ? eventBundle : eventBundle with { Signature = signer.Sign(BusEnvelopeCanonical.Of(eventBundle)) };
    }

    private async Task AppendRangeCoreAsync<TAggregate>(Guid streamId, IEnumerable<object> events,
        EventSubject? statedSubject, CancellationToken cancellationToken) where TAggregate : notnull, new()
    {
        if (!_streamVersions.ContainsKey(streamId))
        {
            await using var transaction = await unitOfWork.StartAsync(cancellationToken);
            var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);
            _streamVersions[streamId] = await eventStreamRepository.GetVersionOrDefaultAsync(streamId, cancellationToken);
        }

        await AddEventsToStreamAsync<TAggregate>(streamId, events, statedSubject, cancellationToken);
    }

    private async Task AddEventsToStreamAsync<TAggregate>(Guid streamId, IEnumerable<object> events,
        EventSubject? statedSubject, CancellationToken cancellationToken) where TAggregate : notnull, new()
    {
        foreach (var @event in events)
        {
            await AppendEventToStreamAsync<TAggregate>(streamId, @event, statedSubject, cancellationToken);
        }
    }

    private async Task AppendEventToStreamAsync<TAggregate>(Guid streamId, object @event, EventSubject? statedSubject,
        CancellationToken cancellationToken) where TAggregate : notnull, new()
    {
        var session = sessionContextProvider.Current
                      ?? throw new SessionRequiredException("Session context is not set");
        var correlationId = session.CorrelationId;
        var causationId = session.CausationId;

        var streamVersion = _streamVersions[streamId] + 1;

        var subject = statedSubject ?? await ResolveSubjectAsync(streamId, @event, session, cancellationToken);
        var dataJson = await serializer.SerializeAsync(@event, subject.TenantId, subject.UserId, cancellationToken);

        var eventStreamEntry = new EventStreamEntry
        {
            Id = Guid.CreateVersion7(),
            StreamId = streamId,
            Version = streamVersion,
            EventTypeName = @event.GetType().GetQualifiedTypeName(),
            AggregateTypeName = typeof(TAggregate).GetQualifiedTypeName(),
            DataJson = dataJson,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
            CausationId = causationId,
            BucketId = BucketCalculator.GetBucketId(streamId),
            ActorTenantId = session.ActorTenantId,
            ActorUserId = session.ActorUserId,
            TenantId = subject.TenantId,
            UserId = subject.UserId,
        };

        _eventStreamEntries.Add(eventStreamEntry);
        _streamVersions[streamId] = streamVersion;

        // A stated Subject is for its one event. Only on a stream's first event is it also the
        // owner the stream records, and so the one the rest of the batch keeps.
        if (statedSubject is null || streamVersion == 1)
        {
            _streamSubjects[streamId] = subject;
        }

        ApplicationDiagnostics.Metrics.EventsAppended.Add(
            1,
            new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.EventType, @event.GetType().Name),
            new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.AggregateType, typeof(TAggregate).Name));
    }

    /// <summary>
    /// Resolve the Subject (data owner) of an event that has none stated, in this priority order —
    /// a Subject stated with AppendOnBehalfOfAsync outranks all of them and is never resolved here:
    /// 1. Per-batch cache (the owner an earlier event in the same SaveChanges resolved for this
    ///    stream — never a Subject stated for another event, except on the stream's first event)
    /// 2. The owner recorded on the stream's first entry — its tenant, and its user where one was
    ///    recorded — for any aggregate type: a stream keeps the owner it was created with, whatever
    ///    session appends to it later
    /// 3. Event payload's IAggregateCreationEvent.TenantId
    /// 4. SessionContext.TenantId fallback
    /// 5. Hard failure if Subject still unresolved (all candidates empty)
    /// </summary>
    private async Task<EventSubject> ResolveSubjectAsync(
        Guid streamId, object @event, SessionContext session, CancellationToken cancellationToken)
    {
        if (_streamSubjects.TryGetValue(streamId, out var cachedSubject))
        {
            return cachedSubject;
        }

        if (await LookupStreamOwnerAsync(streamId, cancellationToken) is { } streamOwner)
        {
            return streamOwner;
        }

        if (@event is IAggregateCreationEvent creation && creation.TenantId != Guid.Empty)
        {
            return new EventSubject(creation.TenantId);
        }

        if (session.TenantId != Guid.Empty)
        {
            return new EventSubject(session.TenantId, session.UserId);
        }

        throw new InvalidOperationException(
            $"Cannot resolve Subject for event {@event.GetType().Name} on stream {streamId}. " +
            "Pass an explicit Subject via AppendOnBehalfOfAsync, mark creation events with IAggregateCreationEvent, " +
            "or set SessionContext.TenantId before appending.");
    }

    private async Task<EventSubject?> LookupStreamOwnerAsync(Guid streamId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);
        if (!await eventStreamRepository.StreamExistsAsync(streamId, cancellationToken))
        {
            return null;
        }

        var firstEntry = await eventStreamRepository.GetFirstOrDefaultAsync(streamId, cancellationToken);
        return firstEntry is { TenantId: var tenantId } && tenantId != Guid.Empty
            ? new EventSubject(tenantId, firstEntry.UserId)
            : null;
    }
}
