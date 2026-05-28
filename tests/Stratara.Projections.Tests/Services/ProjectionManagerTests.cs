using Microsoft.Extensions.Logging;
using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Projections.Tests.Services;

public class ProjectionManagerTests
{
    private readonly Mock<IProjectionHandler> _handlerMock = new();
    private readonly Mock<ILogger<ProjectionManager>> _loggerMock = new();

    private sealed class TestProjectionA : IProjection;
    private sealed class TestProjectionB : IProjection;

    private sealed record TestEventA;
    private sealed record TestEventB;

    private ProjectionManager CreateManager(params IProjection[] projections)
    {
        return new ProjectionManager(_loggerMock.Object, _handlerMock.Object, projections);
    }

    private static Mock<IEvent> CreateEvent(string eventTypeName)
    {
        var mock = new Mock<IEvent>();
        mock.Setup(e => e.EventTypeName).Returns(eventTypeName);
        mock.Setup(e => e.Data).Returns(new object());
        mock.Setup(e => e.StreamId).Returns(Guid.NewGuid());
        mock.Setup(e => e.Version).Returns(1);
        return mock;
    }

    [Fact]
    public async Task HandleAsync_NoProjections_CompletesSuccessfully()
    {
        var manager = CreateManager();
        var events = new List<IEvent> { CreateEvent("SomeEvent").Object };

        var exception = await Record.ExceptionAsync(() => manager.HandleAsync(events, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task HandleAsync_NoRelevantEvents_SkipsProjection()
    {
        var projection = new TestProjectionA();
        var manager = CreateManager(projection);

        _handlerMock.Setup(h => h.GetRelevantEventTypeNames(projection)).Returns(["RelevantEvent"]);

        var events = new List<IEvent> { CreateEvent("IrrelevantEvent").Object };

        await manager.HandleAsync(events, CancellationToken.None);

        _handlerMock.Verify(h => h.ProjectAsync(projection, It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_RelevantEvents_CallsProjectAsync()
    {
        var projection = new TestProjectionA();
        var manager = CreateManager(projection);

        _handlerMock.Setup(h => h.GetRelevantEventTypeNames(projection)).Returns(["TestEventA"]);
        _handlerMock.Setup(h => h.GetProjectionName(projection)).Returns("TestProjectionA");

        var events = new List<IEvent> { CreateEvent("TestEventA").Object };

        await manager.HandleAsync(events, CancellationToken.None);

        _handlerMock.Verify(h => h.ProjectAsync(projection,
            It.Is<IReadOnlyList<IEvent>>(e => e.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_MultipleProjections_ProcessesAll()
    {
        var projectionA = new TestProjectionA();
        var projectionB = new TestProjectionB();
        var manager = CreateManager(projectionA, projectionB);

        _handlerMock.Setup(h => h.GetRelevantEventTypeNames(projectionA)).Returns(["TestEventA"]);
        _handlerMock.Setup(h => h.GetRelevantEventTypeNames(projectionB)).Returns(["TestEventA"]);
        _handlerMock.Setup(h => h.GetProjectionName(It.IsAny<IProjection>())).Returns("Projection");

        var events = new List<IEvent> { CreateEvent("TestEventA").Object };

        await manager.HandleAsync(events, CancellationToken.None);

        _handlerMock.Verify(h => h.ProjectAsync(projectionA, It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Once);
        _handlerMock.Verify(h => h.ProjectAsync(projectionB, It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_FiltersEventsPerProjection()
    {
        var projectionA = new TestProjectionA();
        var projectionB = new TestProjectionB();
        var manager = CreateManager(projectionA, projectionB);

        _handlerMock.Setup(h => h.GetRelevantEventTypeNames(projectionA)).Returns(["TestEventA"]);
        _handlerMock.Setup(h => h.GetRelevantEventTypeNames(projectionB)).Returns(["TestEventB"]);
        _handlerMock.Setup(h => h.GetProjectionName(It.IsAny<IProjection>())).Returns("Projection");

        var events = new List<IEvent>
        {
            CreateEvent("TestEventA").Object,
            CreateEvent("TestEventB").Object
        };

        await manager.HandleAsync(events, CancellationToken.None);

        _handlerMock.Verify(h => h.ProjectAsync(projectionA,
            It.Is<IReadOnlyList<IEvent>>(e => e.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        _handlerMock.Verify(h => h.ProjectAsync(projectionB,
            It.Is<IReadOnlyList<IEvent>>(e => e.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_EmptyEventList_CompletesSuccessfully()
    {
        var projection = new TestProjectionA();
        var manager = CreateManager(projection);

        _handlerMock.Setup(h => h.GetRelevantEventTypeNames(projection)).Returns(["TestEventA"]);
        _handlerMock.Setup(h => h.GetProjectionName(projection)).Returns("TestProjectionA");

        var events = new List<IEvent>();

        await manager.HandleAsync(events, CancellationToken.None);

        _handlerMock.Verify(h => h.ProjectAsync(It.IsAny<IProjection>(), It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ProjectionThrows_PropagatesException()
    {
        var projection = new TestProjectionA();
        var manager = CreateManager(projection);

        _handlerMock.Setup(h => h.GetRelevantEventTypeNames(projection)).Returns(["TestEventA"]);
        _handlerMock.Setup(h => h.GetProjectionName(projection)).Returns("TestProjectionA");
        _handlerMock.Setup(h => h.ProjectAsync(projection, It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Projection failed"));

        var events = new List<IEvent> { CreateEvent("TestEventA").Object };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.HandleAsync(events, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_MixedRelevance_OnlyRelevantEventsProcessed()
    {
        var projection = new TestProjectionA();
        var manager = CreateManager(projection);

        _handlerMock.Setup(h => h.GetRelevantEventTypeNames(projection)).Returns(["TestEventA"]);
        _handlerMock.Setup(h => h.GetProjectionName(projection)).Returns("TestProjectionA");

        var events = new List<IEvent>
        {
            CreateEvent("TestEventA").Object,
            CreateEvent("IrrelevantEvent").Object,
            CreateEvent("TestEventA").Object
        };

        await manager.HandleAsync(events, CancellationToken.None);

        _handlerMock.Verify(h => h.ProjectAsync(projection,
            It.Is<IReadOnlyList<IEvent>>(e => e.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
