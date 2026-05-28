using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Sagas.Abstractions;
using Stratara.Sagas.Services;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Sagas.Tests.Services;

public class SagaManagerTests
{
    private const string OrderPlacedType = "Test.OrderPlaced, Stratara.Sagas.Tests";
    private const string PaymentReceivedType = "Test.PaymentReceived, Stratara.Sagas.Tests";

    public sealed class SagaA : ISaga;

    public sealed class SagaB : ISaga;

    [Fact]
    public async Task HandleAsync_DispatchesEveryRegisteredSagaInParallel()
    {
        var handler = new Mock<ISagaHandler>();
        var sagaA = new SagaA();
        var sagaB = new SagaB();
        handler.Setup(h => h.GetRelevantEventTypeNames(sagaA)).Returns([OrderPlacedType]);
        handler.Setup(h => h.GetRelevantEventTypeNames(sagaB)).Returns([OrderPlacedType]);
        handler.Setup(h => h.GetSagaName(It.IsAny<ISaga>())).Returns<ISaga>(s => s.GetType().Name);

        var events = new IEvent[] { Event(OrderPlacedType), Event(OrderPlacedType) };
        var sut = new SagaManager(NullLogger<SagaManager>.Instance, handler.Object, [sagaA, sagaB]);

        await sut.HandleAsync(events, CancellationToken.None);

        handler.Verify(h => h.HandleAsync(sagaA, It.Is<IReadOnlyList<IEvent>>(e => e.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
        handler.Verify(h => h.HandleAsync(sagaB, It.Is<IReadOnlyList<IEvent>>(e => e.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_FiltersEventsPerSagaByRelevantTypeNames()
    {
        var handler = new Mock<ISagaHandler>();
        var sagaA = new SagaA();
        var sagaB = new SagaB();
        handler.Setup(h => h.GetRelevantEventTypeNames(sagaA)).Returns([OrderPlacedType]);
        handler.Setup(h => h.GetRelevantEventTypeNames(sagaB)).Returns([PaymentReceivedType]);
        handler.Setup(h => h.GetSagaName(It.IsAny<ISaga>())).Returns<ISaga>(s => s.GetType().Name);

        var events = new IEvent[]
        {
            Event(OrderPlacedType),
            Event(PaymentReceivedType),
            Event(OrderPlacedType),
        };
        var sut = new SagaManager(NullLogger<SagaManager>.Instance, handler.Object, [sagaA, sagaB]);

        await sut.HandleAsync(events, CancellationToken.None);

        handler.Verify(
            h => h.HandleAsync(sagaA, It.Is<IReadOnlyList<IEvent>>(e => e.Count == 2 && e.All(x => x.EventTypeName == OrderPlacedType)), It.IsAny<CancellationToken>()),
            Times.Once);
        handler.Verify(
            h => h.HandleAsync(sagaB, It.Is<IReadOnlyList<IEvent>>(e => e.Count == 1 && e.All(x => x.EventTypeName == PaymentReceivedType)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SagaWithNoMatchingEvents_IsNotDispatched()
    {
        var handler = new Mock<ISagaHandler>();
        var sagaA = new SagaA();
        handler.Setup(h => h.GetRelevantEventTypeNames(sagaA)).Returns([PaymentReceivedType]);
        handler.Setup(h => h.GetSagaName(sagaA)).Returns(nameof(SagaA));

        var events = new IEvent[] { Event(OrderPlacedType) };
        var sut = new SagaManager(NullLogger<SagaManager>.Instance, handler.Object, [sagaA]);

        await sut.HandleAsync(events, CancellationToken.None);

        handler.Verify(
            h => h.HandleAsync(It.IsAny<ISaga>(), It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_NoRegisteredSagas_DoesNotThrow()
    {
        var handler = new Mock<ISagaHandler>();
        var sut = new SagaManager(NullLogger<SagaManager>.Instance, handler.Object, []);

        await sut.HandleAsync([Event(OrderPlacedType)], CancellationToken.None);

        handler.Verify(
            h => h.HandleAsync(It.IsAny<ISaga>(), It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static IEvent Event(string typeName)
    {
        var mock = new Mock<IEvent>();
        mock.SetupGet(e => e.EventTypeName).Returns(typeName);
        return mock.Object;
    }
}
