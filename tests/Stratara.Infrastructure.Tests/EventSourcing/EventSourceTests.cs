using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stratara.Contracts.Session;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Shared.EventSourcing;

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

    private sealed record TestCreated(string Name);
    private sealed record TestRenamed(string NewName);

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

        Assert.Single(_capturedAddRangeCalls);
        var entries = _capturedAddRangeCalls[0];
        Assert.Single(entries);
        Assert.Equal(1, entries[0].Version);
        Assert.Equal(streamId, entries[0].StreamId);
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

        Assert.Single(_capturedAddRangeCalls);
        var entries = _capturedAddRangeCalls[0];
        Assert.Single(entries);
        Assert.Equal(4, entries[0].Version);
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

        Assert.Single(_capturedAddRangeCalls);
        Assert.Equal(2, _capturedAddRangeCalls[0].Count);
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

        Assert.Single(_capturedAddRangeCalls);
        var entry = _capturedAddRangeCalls[0][0];
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

        Assert.Single(_capturedAddRangeCalls);
        var entries = _capturedAddRangeCalls[0];
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

    private static DbUpdateException CreateUniqueViolationDbUpdateException() =>
        new("unique constraint violation",
            new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505"));
}
