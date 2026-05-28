using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Merging.ChangeTracking;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.Commands;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Merging.ChangeTracking;

namespace Stratara.Infrastructure.Tests.EventSourcing;

public class ChangeSetHandlerTests
{
    private readonly Mock<ILogger<ChangeSetHandler>> _loggerMock = new();
    private readonly Mock<IEventSource> _eventSourceMock = new();
    private readonly Mock<IAggregationService> _aggregationServiceMock = new();
    private readonly ChangeSetHandler _handler;

    public ChangeSetHandlerTests()
    {
        _handler = new ChangeSetHandler(_loggerMock.Object, _eventSourceMock.Object, _aggregationServiceMock.Object);
    }

    [Fact]
    public async Task CreateChangeSetAsync_AggregateNotFound_ThrowsInvalidOperationException()
    {
        var command = new TestUpdateCommand(Guid.NewGuid(), 1, "NewValue");

        _aggregationServiceMock
            .Setup(a => a.AggregateAsync<TestAggregate>(command.AggregateId, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestAggregate?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.CreateChangeSetAsync<TestAggregate, TestUpdateCommand>(command));
    }

    [Fact]
    public async Task CreateChangeSetAsync_ValidAggregates_ReturnsChangeSet()
    {
        var aggregateId = Guid.NewGuid();
        var command = new TestUpdateCommand(aggregateId, 1, "NewValue");

        var sourceAggregate = new TestAggregate { Name = "OldValue" };
        var currentAggregate = new TestAggregate { Name = "OldValue" };

        _aggregationServiceMock
            .Setup(a => a.AggregateAsync<TestAggregate>(aggregateId, It.IsAny<long?>(), (long?)1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceAggregate);
        _aggregationServiceMock
            .Setup(a => a.AggregateAsync<TestAggregate>(aggregateId, It.IsAny<long?>(), (long?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentAggregate);

        var result = await _handler.CreateChangeSetAsync<TestAggregate, TestUpdateCommand>(command);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ApplyChangeSetAsync_EmptyChangeSet_DoesNotCallEventSource()
    {
        var aggregateId = Guid.NewGuid();
        var emptyChangeSet = new List<ChangeDetail>();

        await _handler.ApplyChangeSetAsync<TestAggregate>(aggregateId, emptyChangeSet);

        _eventSourceMock.Verify(
            e => e.AppendAsync<TestAggregate>(It.IsAny<Guid>(), It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _eventSourceMock.Verify(e => e.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyChangeSetAsync_WithChanges_AppendsEventsAndSaves()
    {
        var aggregateId = Guid.NewGuid();
        var changeSet = new List<ChangeDetail>
        {
            new("Name", "OldValue", "OldValue", "NewValue"),
            new("Description", "", "", "NewDesc")
        };

        _eventSourceMock
            .Setup(e => e.AppendAsync<TestAggregate>(aggregateId, It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _eventSourceMock
            .Setup(e => e.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _handler.ApplyChangeSetAsync<TestAggregate>(aggregateId, changeSet);

        _eventSourceMock.Verify(
            e => e.AppendAsync<TestAggregate>(aggregateId, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _eventSourceMock.Verify(e => e.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    public class TestAggregate
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private sealed record TestUpdateCommand(Guid AggregateId, long SourceVersion, string Name) : IUpdateCommand;
}
