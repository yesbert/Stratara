using Microsoft.EntityFrameworkCore;
using Npgsql;
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
/// (via <see cref="AppendOnBehalfOfAsync{TAggregate}"/>), per-batch cache, existing aggregate's
/// TenantId, <see cref="IAggregateCreationEvent"/> payload, then <see cref="SessionContext"/> fallback.
/// If none yields a non-empty Subject, the append fails fast with an <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// Concurrency conflicts (unique-index violations on stream version, PostgreSQL <c>23505</c>) are
/// surfaced as <see cref="ConcurrencyException"/> and recorded in
/// <c>ApplicationDiagnostics.Metrics.EventSourceAppendConflicts</c>.
/// </para>
/// </remarks>
internal sealed class EventSource(
    ISnapshotService snapshotService,
    IWriteUnitOfWork unitOfWork,
    ISessionContextProvider sessionContextProvider,
    IEventBundleOutboxDispatcher outboxDispatcher,
    ISecureJsonSerializer serializer,
    IBusEnvelopeSigner? signer = null) : IEventSource
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly List<EventStreamEntry> _eventStreamEntries = [];
    private readonly Dictionary<Guid, long> _streamVersions = new();

    /// <remarks>
    /// Per-batch Subject cache. Once a stream's Subject is resolved (via aggregate lookup or creation
    /// event), subsequent events in the same SaveChanges call reuse it without re-querying the database.
    /// </remarks>
    private readonly Dictionary<Guid, EventSubject> _streamSubjects = new();

    /// <remarks>
    /// Per-event explicit Subject override (set by AppendOnBehalfOfAsync), keyed by the event object
    /// identity. Cleared after the entry is materialized.
    /// </remarks>
    private readonly Dictionary<object, EventSubject> _explicitSubjectOverrides = new(ReferenceEqualityComparer.Instance);

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
        await AddEventsToStreamAsync<TAggregate>(streamId, events, cancellationToken);
    }

    /// <inheritdoc/>
    public Task AppendAsync<TAggregate>(Guid streamId, object @event, CancellationToken cancellationToken = default)
        where TAggregate : notnull, new() => AppendRangeAsync<TAggregate>(streamId, [@event], cancellationToken);

    /// <inheritdoc/>
    public async Task AppendRangeAsync<TAggregate>(Guid streamId, IEnumerable<object> events,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new()
    {
        if (!_streamVersions.ContainsKey(streamId))
        {
            await using var transaction = await unitOfWork.StartAsync(cancellationToken);
            var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);
            _streamVersions[streamId] = await eventStreamRepository.GetVersionOrDefaultAsync(streamId, cancellationToken);
        }

        await AddEventsToStreamAsync<TAggregate>(streamId, events, cancellationToken);
    }

    /// <inheritdoc/>
    public Task AppendOnBehalfOfAsync<TAggregate>(Guid streamId, object @event, EventSubject subject,
        CancellationToken cancellationToken = default) where TAggregate : notnull, new()
    {
        _explicitSubjectOverrides[@event] = subject;
        return AppendAsync<TAggregate>(streamId, @event, cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ConcurrencyException">
    /// Thrown when an optimistic-concurrency conflict (duplicate stream-version) is detected while
    /// persisting the buffered events.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown when no <see cref="SessionContext"/> is set on the current scope.</exception>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);

        await eventStreamRepository.AddRangeAsync(_eventStreamEntries, cancellationToken);
        await snapshotService.AddSnapshotIfNeededAsync(_eventStreamEntries, cancellationToken);

        try
        {
            await transaction.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsConcurrencyConflict(ex))
        {
            var firstEntry = _eventStreamEntries.FirstOrDefault();
            var streamId = firstEntry?.StreamId ?? Guid.Empty;
            var aggregateTypeName = firstEntry?.AggregateTypeName ?? string.Empty;
            var bucketId = firstEntry?.BucketId ?? 0;
            ClearBatchState();
            ApplicationDiagnostics.Metrics.EventSourceAppendConflicts.Add(1,
                new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.AggregateType, aggregateTypeName),
                new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.BucketId, bucketId));
            throw new ConcurrencyException(streamId, aggregateTypeName, ex);
        }

        await PublishEventBundleAsync(_eventStreamEntries, cancellationToken);
        ClearBatchState();
    }

    private void ClearBatchState()
    {
        _eventStreamEntries.Clear();
        _streamVersions.Clear();
        _streamSubjects.Clear();
        _explicitSubjectOverrides.Clear();
    }

    private static bool IsConcurrencyConflict(DbUpdateException ex)
    {
        if (ex is DbUpdateConcurrencyException)
        {
            return true;
        }

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException pg && pg.SqlState == PostgresUniqueViolationSqlState)
            {
                return true;
            }
        }

        return false;
    }

    private async Task PublishEventBundleAsync(IReadOnlyList<EventStreamEntry> eventEntries, CancellationToken cancellationToken = default)
    {
        var sessionContext = sessionContextProvider.Current ?? throw new InvalidOperationException("Session context is not set");
        var eventBundle = eventEntries.MapToEventBundle(sessionContext);
        if (signer is not null)
        {
            eventBundle = eventBundle with { Signature = signer.Sign(BusEnvelopeCanonical.Of(eventBundle)) };
        }
        await outboxDispatcher.EnqueueEventBundleAsync(eventBundle, cancellationToken);
    }

    private async Task AddEventsToStreamAsync<TAggregate>(Guid streamId, IEnumerable<object> events, CancellationToken cancellationToken)
        where TAggregate : notnull, new()
    {
        foreach (var @event in events)
        {
            await AppendEventToStreamAsync<TAggregate>(streamId, @event, cancellationToken);
        }
    }

    private async Task AppendEventToStreamAsync<TAggregate>(Guid streamId, object @event, CancellationToken cancellationToken)
        where TAggregate : notnull, new()
    {
        var session = sessionContextProvider.Current
                      ?? throw new InvalidOperationException("Session context is not set");
        var correlationId = session.CorrelationId;
        var causationId = session.CausationId;

        var streamVersion = _streamVersions[streamId] + 1;

        var subject = await ResolveSubjectAsync<TAggregate>(streamId, @event, session, cancellationToken);
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
        _streamSubjects[streamId] = subject;
        _explicitSubjectOverrides.Remove(@event);
    }

    /// <summary>
    /// Resolve Subject (data owner) for an event in this priority order:
    /// 1. Explicit override (set by AppendOnBehalfOfAsync)
    /// 2. Per-batch cache (previous event in the same SaveChanges resolved Subject for this stream)
    /// 3. Existing aggregate's TenantId (for ITenantAggregate streams that already exist)
    /// 4. Event payload's IAggregateCreationEvent.TenantId
    /// 5. SessionContext.TenantId fallback
    /// 6. Hard failure if Subject still unresolved (all candidates empty)
    /// </summary>
    private async Task<EventSubject> ResolveSubjectAsync<TAggregate>(
        Guid streamId, object @event, SessionContext session, CancellationToken cancellationToken)
        where TAggregate : notnull, new()
    {
        if (_explicitSubjectOverrides.TryGetValue(@event, out var explicitSubject))
        {
            return explicitSubject;
        }

        if (_streamSubjects.TryGetValue(streamId, out var cachedSubject))
        {
            return cachedSubject;
        }

        if (typeof(ITenantAggregate).IsAssignableFrom(typeof(TAggregate)))
        {
            var existingTenantId = await LookupExistingAggregateTenantIdAsync(streamId, cancellationToken);
            if (existingTenantId.HasValue && existingTenantId.Value != Guid.Empty)
            {
                return new EventSubject(existingTenantId.Value);
            }
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

    private async Task<Guid?> LookupExistingAggregateTenantIdAsync(Guid streamId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);
        if (!await eventStreamRepository.StreamExistsAsync(streamId, cancellationToken))
        {
            return null;
        }

        var firstEntry = await eventStreamRepository.GetFirstOrDefaultAsync(streamId, cancellationToken);
        return firstEntry?.TenantId;
    }
}
