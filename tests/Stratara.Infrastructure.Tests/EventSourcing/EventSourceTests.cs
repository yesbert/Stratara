using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stratara.Contracts.Session;
using Stratara.Domain;
using Stratara.Domain.Multitenancy;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Shared.EventSourcing;

using Stratara.Infrastructure.Tests.Diagnostics;

namespace Stratara.Infrastructure.Tests.EventSourcing;

public class EventSourceTests
{
    private readonly Mock<ISnapshotService> _snapshotServiceMock = new();
    private readonly Mock<IWriteUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ISessionContextProvider> _sessionContextProviderMock = new();
    private readonly Mock<IEventBundleOutboxDispatcher> _outboxDispatcherMock = new();
    private readonly Mock<ISecureJsonSerializer> _serializerMock = new();
    private readonly Mock<ITransaction> _transactionMock = new();
    private readonly Mock<IEventStreamRepository> _eventStreamRepoMock = new();
    private readonly EventSource _eventSource;
    private readonly List<List<EventStreamEntry>> _capturedAddRangeCalls = [];

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public EventSourceTests()
    {
        _unitOfWorkMock.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_transactionMock.Object);
        _unitOfWorkMock.Setup(u => u.CreateEventStreamRepository(_transactionMock.Object)).Returns(_eventStreamRepoMock.Object);

        var sessionContext = new SessionContext("corr-1", "caus-1", null, _tenantId, _userId, _tenantId, null);
        _sessionContextProviderMock.Setup(s => s.Current).Returns(sessionContext);

        _serializerMock.Setup(s => s.SerializeAsync(It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");

        _eventStreamRepoMock
            .Setup(r => r.AddRangeAsync(It.IsAny<IReadOnlyList<EventStreamEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<EventStreamEntry>, CancellationToken>((entries, _) =>
                _capturedAddRangeCalls.Add(entries.ToList()))
            .Returns(Task.CompletedTask);

        _eventSource = new EventSource(
            _snapshotServiceMock.Object,
            _unitOfWorkMock.Object,
            _sessionContextProviderMock.Object,
            _outboxDispatcherMock.Object,
            _serializerMock.Object);
    }

    private sealed class TestAggregate
    {
        public string Name { get; set; } = "";
    }

    private sealed class TenantScopedAggregate : ITenantAggregate
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    private sealed record TestCreated(string Name);
    private sealed record TestRenamed(string NewName);

    private static EventStreamEntry EntryOwnedBy(Guid streamId, Guid ownerTenantId) => new()
    {
        StreamId = streamId,
        Version = 1,
        EventTypeName = "Existing",
        AggregateTypeName = "Existing",
        DataJson = "{}",
        BucketId = 0,
        TenantId = ownerTenantId,
        ActorTenantId = ownerTenantId,
        ActorUserId = Guid.NewGuid(),
    };

    private void GivenAnExistingStreamOwnedBy(Guid streamId, Guid ownerTenantId)
    {
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _eventStreamRepoMock.Setup(r => r.GetVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(1L);
        _eventStreamRepoMock.Setup(r => r.GetFirstOrDefaultAsync(streamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EntryOwnedBy(streamId, ownerTenantId));
    }

    [Fact]
    public async Task ExistsAsync_DelegatesToRepository()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _eventSource.ExistsAsync(streamId);

        Assert.True(result);
        _eventStreamRepoMock.Verify(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCurrentVersionAsync_DelegatesToRepository()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.GetVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(5L);

        var result = await _eventSource.GetCurrentVersionAsync(streamId);

        Assert.Equal(5L, result);
    }

    [Fact]
    public async Task CreateAsync_NewStream_AddsEventWithVersion1()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));

        await _eventSource.SaveChangesAsync();

        var entries = Assert.Single(_capturedAddRangeCalls);
        var entry = Assert.Single(entries);
        Assert.Equal(1, entry.Version);
        Assert.Equal(streamId, entry.StreamId);
    }

    [Fact]
    public async Task CreateAsync_ExistingStream_ThrowsInvalidOperationException()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test")));
    }

    [Fact]
    public async Task AppendAsync_IncrementsVersion()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.GetVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(3L);

        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("New"));

        await _eventSource.SaveChangesAsync();

        var entries = Assert.Single(_capturedAddRangeCalls);
        var entry = Assert.Single(entries);
        Assert.Equal(4, entry.Version);
    }

    [Fact]
    public async Task AppendAsync_LazyLoadsCurrentVersion()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.GetVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(0L);

        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("First"));
        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("Second"));

        _eventStreamRepoMock.Verify(r => r.GetVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveChangesAsync_PersistsAllBufferedEvents()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));
        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("Renamed"));

        await _eventSource.SaveChangesAsync();

        var entries = Assert.Single(_capturedAddRangeCalls);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task SaveChangesAsync_PublishesEventBundle()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));
        await _eventSource.SaveChangesAsync();

        _outboxDispatcherMock.Verify(o => o.EnqueueEventBundleAsync(
            It.IsAny<Contracts.Messages.EventBundle>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveChangesAsync_CallsSnapshotService()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));
        await _eventSource.SaveChangesAsync();

        _snapshotServiceMock.Verify(s => s.AddSnapshotIfNeededAsync(
            It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveChangesAsync_ClearsBuffer()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));
        await _eventSource.SaveChangesAsync();

        await _eventSource.SaveChangesAsync();

        Assert.Equal(2, _capturedAddRangeCalls.Count);
        Assert.Single(_capturedAddRangeCalls[0]);
        Assert.Empty(_capturedAddRangeCalls[1]);
    }

    [Fact]
    public async Task CreateAsync_CapturesSessionContext()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));
        await _eventSource.SaveChangesAsync();

        var entries = Assert.Single(_capturedAddRangeCalls);
        var entry = entries[0];
        Assert.Equal(_tenantId, entry.TenantId);
        Assert.Equal(_tenantId, entry.ActorTenantId);
        Assert.Equal(_userId, entry.ActorUserId);
        Assert.Equal("corr-1", entry.CorrelationId);
    }

    [Fact]
    public async Task CreateRangeAsync_MultipleEvents_IncrementsVersionSequentially()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var events = new object[] { new TestCreated("Test"), new TestRenamed("Renamed") };
        await _eventSource.CreateRangeAsync<TestAggregate>(streamId, events);

        await _eventSource.SaveChangesAsync();

        var entries = Assert.Single(_capturedAddRangeCalls);
        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries[0].Version);
        Assert.Equal(2, entries[1].Version);
    }

    [Fact]
    public async Task SaveChangesAsync_NoSessionContext_ThrowsInvalidOperationException()
    {
        _sessionContextProviderMock.Setup(s => s.Current).Returns((SessionContext?)null);

        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test")));
    }

    [Fact]
    public async Task SaveChangesAsync_OnPostgresUniqueViolation_ThrowsConcurrencyExceptionWithStreamInfo()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _transactionMock.Setup(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(CreateUniqueViolationDbUpdateException());

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => _eventSource.SaveChangesAsync());

        Assert.Equal(streamId, ex.StreamId);
        Assert.Contains(nameof(TestAggregate), ex.AggregateTypeName);
    }

    [Fact]
    public async Task SaveChangesAsync_OnConflict_RecordsTheAppendConflictCounter()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _transactionMock.Setup(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(CreateUniqueViolationDbUpdateException());

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));

        var (listener, measurements) = MeterCapture.Start();
        using (listener)
        {
            await Assert.ThrowsAsync<ConcurrencyException>(() => _eventSource.SaveChangesAsync());
        }

        var conflicts = measurements.Snapshot()
            .Where(m => m.Instrument == "event_source.append.conflicts")
            .Where(m => (m.Tags.GetValueOrDefault("aggregate.type") as string)?.Contains(nameof(TestAggregate), StringComparison.Ordinal) == true)
            .ToList();

        var conflict = Assert.Single(conflicts);
        Assert.Equal(1, conflict.Value);
        Assert.True(conflict.Tags.ContainsKey("bucket.id"));
    }

    [Fact]
    public async Task SaveChangesAsync_OnPostgresUniqueViolation_ClearsBuffer()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _transactionMock.SetupSequence(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(CreateUniqueViolationDbUpdateException())
            .ReturnsAsync(0);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));
        await Assert.ThrowsAsync<ConcurrencyException>(() => _eventSource.SaveChangesAsync());

        // After the conflict, the EventSource's internal buffers must be empty so the next call doesn't
        // re-emit stale entries from the failed attempt.
        await _eventSource.SaveChangesAsync();

        Assert.Equal(2, _capturedAddRangeCalls.Count);
        Assert.Single(_capturedAddRangeCalls[0]);
        Assert.Empty(_capturedAddRangeCalls[1]);
    }

    [Fact]
    public async Task SaveChangesAsync_OnDbUpdateConcurrencyException_ThrowsConcurrencyException()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _transactionMock.Setup(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException("row version mismatch"));

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => _eventSource.SaveChangesAsync());

        Assert.Equal(streamId, ex.StreamId);
    }

    [Fact]
    public async Task SaveChangesAsync_OnNonConcurrencyDbUpdateException_PropagatesAsIs()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var unrelated = new DbUpdateException("disk full",
            new PostgresException("disk full", "ERROR", "ERROR", "53100"));
        _transactionMock.Setup(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(unrelated);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => _eventSource.SaveChangesAsync());

        Assert.Same(unrelated, thrown);
    }

    [Fact]
    public async Task AppendOnBehalfOfAsync_SubjectNamingATenant_OverridesTheSessionTenant()
    {
        var streamId = Guid.NewGuid();
        var explicitTenantId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.GetVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(0L);

        await _eventSource.AppendOnBehalfOfAsync<TestAggregate>(
            streamId, new TestRenamed("New"), new EventSubject(explicitTenantId));
        await _eventSource.SaveChangesAsync();

        var entry = Assert.Single(_capturedAddRangeCalls[0]);
        Assert.Equal(explicitTenantId, entry.TenantId);
        Assert.NotEqual(_tenantId, entry.TenantId);
        _serializerMock.Verify(
            s => s.SerializeAsync(It.IsAny<object>(), explicitTenantId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AppendOnBehalfOfAsync_SubjectNamingNoTenant_ThrowsNamingTheEventAndStream()
    {
        var streamId = Guid.NewGuid();
        _sessionContextProviderMock.Setup(s => s.Current)
            .Returns(new SessionContext("corr-1", "caus-1", null, Guid.Empty, Guid.Empty, Guid.Empty, null));

        var thrown = await Assert.ThrowsAsync<ArgumentException>(() =>
            _eventSource.AppendOnBehalfOfAsync<TestAggregate>(
                streamId, new TestRenamed("New"), new EventSubject(Guid.Empty)));

        Assert.Equal("subject", thrown.ParamName);
        Assert.Contains(nameof(TestRenamed), thrown.Message, StringComparison.Ordinal);
        Assert.Contains(streamId.ToString(), thrown.Message, StringComparison.Ordinal);

        await _eventSource.SaveChangesAsync();

        Assert.Empty(Assert.Single(_capturedAddRangeCalls));
    }

    [Fact]
    public async Task AppendOnBehalfOfAsync_SubjectNamingNoTenant_DoesNotFallBackToTheSession()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.GetVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(0L);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _eventSource.AppendOnBehalfOfAsync<TestAggregate>(
                streamId, new TestRenamed("New"), new EventSubject(Guid.Empty)));

        await _eventSource.SaveChangesAsync();

        Assert.Empty(Assert.Single(_capturedAddRangeCalls));
        _serializerMock.Verify(
            s => s.SerializeAsync(It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateAsync_TenantCreated_RecordsTheCreatedTenantAsSubject()
    {
        var newTenantId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(newTenantId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<Tenant>(newTenantId,
            new TenantCreated(newTenantId, Guid.NewGuid(), "Acme", "de-DE", true, DateTimeOffset.UtcNow));
        await _eventSource.SaveChangesAsync();

        var entry = Assert.Single(_capturedAddRangeCalls[0]);
        Assert.Equal(newTenantId, entry.TenantId);
        Assert.NotEqual(_tenantId, entry.TenantId);
    }

    private static DbUpdateException CreateUniqueViolationDbUpdateException() =>
        new("unique constraint violation",
            new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505"));

    /// <summary>
    /// The test that would have caught the defect. A plain <c>IAggregate</c> skipped the stream
    /// lookup entirely, so the session won and one stream could end up with two owners.
    /// </summary>
    [Fact]
    public async Task AppendAsync_PlainAggregateOnAnExistingStream_KeepsTheStreamsOwner()
    {
        var streamId = Guid.NewGuid();
        var firstOwner = Guid.NewGuid();
        GivenAnExistingStreamOwnedBy(streamId, firstOwner);

        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("New"));
        await _eventSource.SaveChangesAsync();

        var entry = Assert.Single(Assert.Single(_capturedAddRangeCalls));
        Assert.Equal(firstOwner, entry.TenantId);
        Assert.NotEqual(_tenantId, entry.TenantId);
        Assert.Equal(_tenantId, entry.ActorTenantId);
    }

    [Fact]
    public async Task AppendAsync_TenantAggregateOnAnExistingStream_StillKeepsTheStreamsOwner()
    {
        var streamId = Guid.NewGuid();
        var firstOwner = Guid.NewGuid();
        GivenAnExistingStreamOwnedBy(streamId, firstOwner);

        await _eventSource.AppendAsync<TenantScopedAggregate>(streamId, new TestRenamed("New"));
        await _eventSource.SaveChangesAsync();

        var entry = Assert.Single(Assert.Single(_capturedAddRangeCalls));
        Assert.Equal(firstOwner, entry.TenantId);
        Assert.NotEqual(_tenantId, entry.TenantId);
    }

    /// <summary>
    /// Shared ownership stays available — it just has to be stated. The override outranks the
    /// stream's owner, and a later batch appending normally returns to it.
    /// </summary>
    [Fact]
    public async Task AppendOnBehalfOfAsync_OutranksTheStreamsOwner_AndDoesNotOutliveItsBatch()
    {
        var streamId = Guid.NewGuid();
        var firstOwner = Guid.NewGuid();
        var onBehalfOf = Guid.NewGuid();
        GivenAnExistingStreamOwnedBy(streamId, firstOwner);

        await _eventSource.AppendOnBehalfOfAsync<TestAggregate>(
            streamId, new TestRenamed("Stated"), new EventSubject(onBehalfOf));
        await _eventSource.SaveChangesAsync();

        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("Ordinary"));
        await _eventSource.SaveChangesAsync();

        Assert.Equal(2, _capturedAddRangeCalls.Count);
        Assert.Equal(onBehalfOf, Assert.Single(_capturedAddRangeCalls[0]).TenantId);
        Assert.Equal(firstOwner, Assert.Single(_capturedAddRangeCalls[1]).TenantId);
    }

    /// <summary>
    /// The lookup now runs for every aggregate, so it also runs for a stream that does not exist yet.
    /// A first append must still resolve from the creation event, and from the session where the
    /// event carries no tenant.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NewStream_ResolvesFromTheCreationEvent()
    {
        var newTenantId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(newTenantId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<Tenant>(newTenantId,
            new TenantCreated(newTenantId, Guid.NewGuid(), "Acme", "de-DE", true, DateTimeOffset.UtcNow));
        await _eventSource.SaveChangesAsync();

        Assert.Equal(newTenantId, Assert.Single(Assert.Single(_capturedAddRangeCalls)).TenantId);
    }

    [Fact]
    public async Task CreateAsync_NewStream_FallsBackToTheSessionWhenTheEventNamesNoTenant()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));
        await _eventSource.SaveChangesAsync();

        Assert.Equal(_tenantId, Assert.Single(Assert.Single(_capturedAddRangeCalls)).TenantId);
    }

    [Fact]
    public async Task AppendAsync_NoHistoryNoCreationEventNoSessionTenant_StillFailsNamingTheThreeWays()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _sessionContextProviderMock.Setup(s => s.Current)
            .Returns(new SessionContext("corr-1", "caus-1", null, Guid.Empty, _userId, Guid.Empty, null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("New")));

        Assert.Contains(nameof(TestRenamed), ex.Message, StringComparison.Ordinal);
        Assert.Contains(streamId.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("AppendOnBehalfOfAsync", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IAggregateCreationEvent), ex.Message, StringComparison.Ordinal);
        Assert.Contains("SessionContext.TenantId", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason the change exists. Two tenants writing to one stream used to leave entries owned by
    /// whoever wrote them, so erasing either tenant covered only part of the stream — and once that
    /// tenant's key was shredded, its entries no longer decrypted and the aggregate could not be
    /// rebuilt for anyone. One owner means one erasure covers all of it.
    /// </summary>
    [Fact]
    public async Task TwoTenantsAppendingToOneStream_LeaveOneOwner_SoASingleErasureCoversIt()
    {
        var streamId = Guid.NewGuid();
        var firstOwner = Guid.NewGuid();
        GivenAnExistingStreamOwnedBy(streamId, firstOwner);

        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("by the session tenant"));
        await _eventSource.SaveChangesAsync();
        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("and again"));
        await _eventSource.SaveChangesAsync();

        var recorded = _capturedAddRangeCalls.SelectMany(entries => entries).ToList();
        Assert.Equal(2, recorded.Count);
        Assert.Single(recorded.Select(e => e.TenantId).Distinct());
        Assert.Equal(firstOwner, recorded[0].TenantId);

        _serializerMock.Verify(
            s => s.SerializeAsync(It.IsAny<object>(), firstOwner, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _serializerMock.Verify(
            s => s.SerializeAsync(It.IsAny<object>(), _tenantId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The cost of the change, as a count rather than a claim. The stream-owner lookup now runs for
    /// aggregates that skipped it; the per-batch cache bounds it to once per stream per batch, and
    /// the reads only happen when the stream already exists. Pinned here so a later change on this
    /// write path can be weighed against a number.
    /// </summary>
    [Fact]
    public async Task TheStreamOwnerLookup_RunsAtMostOncePerStreamPerBatch()
    {
        var streamId = Guid.NewGuid();
        GivenAnExistingStreamOwnedBy(streamId, Guid.NewGuid());

        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("one"));
        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("two"));
        await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("three"));
        await _eventSource.SaveChangesAsync();

        _eventStreamRepoMock.Verify(
            r => r.GetFirstOrDefaultAsync(streamId, It.IsAny<CancellationToken>()), Times.Once);
        _eventStreamRepoMock.Verify(
            r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheStreamOwnerLookup_RunsOncePerStream_WhenABatchSpansSeveral()
    {
        var streams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var streamId in streams)
        {
            GivenAnExistingStreamOwnedBy(streamId, Guid.NewGuid());
        }

        foreach (var streamId in streams)
        {
            await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("one"));
            await _eventSource.AppendAsync<TestAggregate>(streamId, new TestRenamed("two"));
        }

        await _eventSource.SaveChangesAsync();

        foreach (var streamId in streams)
        {
            _eventStreamRepoMock.Verify(
                r => r.GetFirstOrDefaultAsync(streamId, It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task TheStreamOwnerLookup_ReadsNothingFurther_WhenTheStreamDoesNotExistYet()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _eventSource.CreateAsync<TestAggregate>(streamId, new TestCreated("Test"));
        await _eventSource.SaveChangesAsync();

        _eventStreamRepoMock.Verify(
            r => r.GetFirstOrDefaultAsync(streamId, It.IsAny<CancellationToken>()), Times.Never);
    }
}
