using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Reflections;

namespace Stratara.Projections.Tests.Services;

public class ProjectionHandlerTests
{
    private readonly Mock<IProjectionMethodInvoker> _invokerMock = new();
    private readonly ProjectionHandler _handler;

    private static readonly Func<IProjection, object, CancellationToken, Task> NoOp = (_, _, _) => Task.CompletedTask;

    public ProjectionHandlerTests()
    {
        _handler = new ProjectionHandler(_invokerMock.Object);
    }

    private sealed class TestProjection : IProjection;

    private sealed record TestEventA;
    private sealed record TestEventB;

    private static Mock<IEvent> CreateEvent(object data)
    {
        var mock = new Mock<IEvent>();
        mock.Setup(e => e.Data).Returns(data);
        mock.Setup(e => e.StreamId).Returns(Guid.NewGuid());
        mock.Setup(e => e.Version).Returns(1);
        return mock;
    }

    [Fact]
    public void GetRelevantEventTypes_DelegatesToInvoker()
    {
        var projection = new TestProjection();
        var expectedTypes = new[] { typeof(TestEventA), typeof(TestEventB) };
        _invokerMock.Setup(i => i.GetOrCreateRelevantEventTypes(projection)).Returns(expectedTypes);

        var result = _handler.GetRelevantEventTypes(projection);

        Assert.Equal(expectedTypes, result);
    }

    [Fact]
    public void GetRelevantEventTypeNames_ReturnsQualifiedNames()
    {
        var projection = new TestProjection();
        var types = new[] { typeof(TestEventA) };
        _invokerMock.Setup(i => i.GetOrCreateRelevantEventTypes(projection)).Returns(types);

        var result = _handler.GetRelevantEventTypeNames(projection);

        Assert.Single(result);
        Assert.Equal(typeof(TestEventA).GetQualifiedTypeName(), result[0]);
    }

    [Fact]
    public void GetProjectionName_ReturnsClassName()
    {
        var projection = new TestProjection();

        var result = _handler.GetProjectionName(projection);

        Assert.Equal("TestProjection", result);
    }

    [Fact]
    public async Task ProjectAsync_DirectHandler_InvokesDelegate()
    {
        var projection = new TestProjection();
        var eventData = new TestEventA();
        var eventMock = CreateEvent(eventData);
        var invoked = false;

        Func<IProjection, object, CancellationToken, Task> handler = (_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        };

        _invokerMock.Setup(i => i.GetOrCreateDelegate(projection, typeof(TestEventA))).Returns(handler);
        _invokerMock.Setup(i => i.IsNoOp(handler)).Returns(false);

        await _handler.ProjectAsync(projection, [eventMock.Object]);

        Assert.True(invoked);
    }

    [Fact]
    public async Task ProjectAsync_NoDirectHandler_FallsBackToWrapped()
    {
        var projection = new TestProjection();
        var eventData = new TestEventA();
        var eventMock = CreateEvent(eventData);
        var wrappedInvoked = false;

        Func<IProjection, object, CancellationToken, Task> wrappedHandler = (_, _, _) =>
        {
            wrappedInvoked = true;
            return Task.CompletedTask;
        };

        _invokerMock.Setup(i => i.GetOrCreateDelegate(projection, typeof(TestEventA))).Returns(NoOp);
        _invokerMock.Setup(i => i.IsNoOp(NoOp)).Returns(true);

        var wrappedType = typeof(IEvent<>).MakeGenericType(typeof(TestEventA));
        _invokerMock.Setup(i => i.GetOrCreateDelegate(projection, wrappedType)).Returns(wrappedHandler);

        await _handler.ProjectAsync(projection, [eventMock.Object]);

        Assert.True(wrappedInvoked);
    }

    [Fact]
    public async Task ProjectAsync_MultipleEvents_InvokesEach()
    {
        var projection = new TestProjection();
        var eventA = CreateEvent(new TestEventA());
        var eventB = CreateEvent(new TestEventB());
        var invokeCount = 0;

        Func<IProjection, object, CancellationToken, Task> handler = (_, _, _) =>
        {
            invokeCount++;
            return Task.CompletedTask;
        };

        _invokerMock.Setup(i => i.GetOrCreateDelegate(projection, It.IsAny<Type>())).Returns(handler);
        _invokerMock.Setup(i => i.IsNoOp(handler)).Returns(false);

        await _handler.ProjectAsync(projection, [eventA.Object, eventB.Object]);

        Assert.Equal(2, invokeCount);
    }

    [Fact]
    public async Task ProjectAsync_DirectHandlerReceivesEventData()
    {
        var projection = new TestProjection();
        var eventData = new TestEventA();
        var eventMock = CreateEvent(eventData);
        object? receivedData = null;

        Func<IProjection, object, CancellationToken, Task> handler = (_, data, _) =>
        {
            receivedData = data;
            return Task.CompletedTask;
        };

        _invokerMock.Setup(i => i.GetOrCreateDelegate(projection, typeof(TestEventA))).Returns(handler);
        _invokerMock.Setup(i => i.IsNoOp(handler)).Returns(false);

        await _handler.ProjectAsync(projection, [eventMock.Object]);

        Assert.Same(eventData, receivedData);
    }

    [Fact]
    public async Task ProjectAsync_WrappedHandlerReceivesFullEvent()
    {
        var projection = new TestProjection();
        var eventData = new TestEventA();
        var eventMock = CreateEvent(eventData);
        object? receivedEvent = null;

        Func<IProjection, object, CancellationToken, Task> wrappedHandler = (_, evt, _) =>
        {
            receivedEvent = evt;
            return Task.CompletedTask;
        };

        _invokerMock.Setup(i => i.GetOrCreateDelegate(projection, typeof(TestEventA))).Returns(NoOp);
        _invokerMock.Setup(i => i.IsNoOp(NoOp)).Returns(true);

        var wrappedType = typeof(IEvent<>).MakeGenericType(typeof(TestEventA));
        _invokerMock.Setup(i => i.GetOrCreateDelegate(projection, wrappedType)).Returns(wrappedHandler);

        await _handler.ProjectAsync(projection, [eventMock.Object]);

        Assert.Same(eventMock.Object, receivedEvent);
    }

    [Fact]
    public async Task ProjectAsync_EmptyEventList_CompletesSuccessfully()
    {
        var projection = new TestProjection();

        await _handler.ProjectAsync(projection, new List<IEvent>());

        _invokerMock.Verify(i => i.GetOrCreateDelegate(It.IsAny<IProjection>(), It.IsAny<Type>()), Times.Never);
    }
}
