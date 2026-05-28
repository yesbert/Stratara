using Stratara.Sagas.Abstractions;
using Stratara.Sagas.Services;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Reflections;

namespace Stratara.Sagas.Tests.Services;

public class SagaHandlerTests
{
    public sealed record OrderPlaced(Guid OrderId);

    public sealed class TestSaga : ISaga;

    private sealed class StubEvent<TPayload>(TPayload data) : IEvent<TPayload> where TPayload : notnull
    {
        public Guid Id { get; } = Guid.NewGuid();
        public long Version => 1;
        public TPayload Data { get; } = data;
        object IEvent.Data => Data;
        public Guid StreamId => Guid.Empty;
        public string EventTypeName => typeof(TPayload).GetQualifiedTypeName();
        public string AggregateTypeName => "TestAggregate";
        public Guid TenantId => Guid.Empty;
        public Guid UserId => Guid.Empty;
    }

    [Fact]
    public async Task HandleAsync_DirectHandlerExists_DispatchesViaDirectDelegate()
    {
        var invoker = new Mock<ISagaMethodInvoker>();
        var saga = new TestSaga();
        var orderEvent = new OrderPlaced(Guid.NewGuid());
        var directCalled = false;
        Func<ISaga, object, CancellationToken, Task> directDelegate = (_, _, _) =>
        {
            directCalled = true;
            return Task.CompletedTask;
        };
        invoker.Setup(i => i.GetOrCreateDelegate(saga, typeof(OrderPlaced))).Returns(directDelegate);
        invoker.Setup(i => i.IsNoOp(directDelegate)).Returns(false);

        var sut = new SagaHandler(invoker.Object);
        await sut.HandleAsync(saga, [new StubEvent<OrderPlaced>(orderEvent)]);

        Assert.True(directCalled);
        invoker.Verify(i => i.GetOrCreateDelegate(saga, typeof(IEvent<OrderPlaced>)), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_DirectHandlerIsNoOp_FallsBackToWrappedHandler()
    {
        var invoker = new Mock<ISagaMethodInvoker>();
        var saga = new TestSaga();
        var orderEvent = new OrderPlaced(Guid.NewGuid());
        var noOpDelegate = (Func<ISaga, object, CancellationToken, Task>)((_, _, _) => Task.CompletedTask);
        var wrappedCalled = false;
        Func<ISaga, object, CancellationToken, Task> wrappedDelegate = (_, _, _) =>
        {
            wrappedCalled = true;
            return Task.CompletedTask;
        };
        invoker.Setup(i => i.GetOrCreateDelegate(saga, typeof(OrderPlaced))).Returns(noOpDelegate);
        invoker.Setup(i => i.IsNoOp(noOpDelegate)).Returns(true);
        invoker.Setup(i => i.GetOrCreateDelegate(saga, typeof(IEvent<OrderPlaced>))).Returns(wrappedDelegate);

        var sut = new SagaHandler(invoker.Object);
        await sut.HandleAsync(saga, [new StubEvent<OrderPlaced>(orderEvent)]);

        Assert.True(wrappedCalled);
    }

    [Fact]
    public async Task HandleAsync_MultipleEvents_DispatchesEachInOrder()
    {
        var invoker = new Mock<ISagaMethodInvoker>();
        var saga = new TestSaga();
        var order = new List<Guid>();
        Func<ISaga, object, CancellationToken, Task> directDelegate = (_, e, _) =>
        {
            order.Add(((OrderPlaced)e).OrderId);
            return Task.CompletedTask;
        };
        invoker.Setup(i => i.GetOrCreateDelegate(saga, typeof(OrderPlaced))).Returns(directDelegate);
        invoker.Setup(i => i.IsNoOp(directDelegate)).Returns(false);

        var e1 = new OrderPlaced(Guid.NewGuid());
        var e2 = new OrderPlaced(Guid.NewGuid());

        var sut = new SagaHandler(invoker.Object);
        await sut.HandleAsync(saga, [new StubEvent<OrderPlaced>(e1), new StubEvent<OrderPlaced>(e2)]);

        Assert.Equal([e1.OrderId, e2.OrderId], order);
    }

    [Fact]
    public void GetSagaName_ReturnsSagaTypeName()
    {
        var sut = new SagaHandler(new Mock<ISagaMethodInvoker>().Object);

        Assert.Equal(nameof(TestSaga), sut.GetSagaName(new TestSaga()));
    }

    [Fact]
    public void GetRelevantEventTypes_DelegatesToInvoker()
    {
        var invoker = new Mock<ISagaMethodInvoker>();
        var saga = new TestSaga();
        invoker.Setup(i => i.GetOrCreateRelevantEventTypes(saga)).Returns([typeof(OrderPlaced)]);

        var sut = new SagaHandler(invoker.Object);

        Assert.Equal([typeof(OrderPlaced)], sut.GetRelevantEventTypes(saga));
    }

    [Fact]
    public void GetRelevantEventTypeNames_ReturnsQualifiedTypeNames()
    {
        var invoker = new Mock<ISagaMethodInvoker>();
        var saga = new TestSaga();
        invoker.Setup(i => i.GetOrCreateRelevantEventTypes(saga)).Returns([typeof(OrderPlaced)]);

        var sut = new SagaHandler(invoker.Object);
        var names = sut.GetRelevantEventTypeNames(saga);

        Assert.Equal([typeof(OrderPlaced).GetQualifiedTypeName()], names);
    }
}
