using JetBrains.Annotations;
using Stratara.Sagas.Abstractions;
using Stratara.Sagas.Services;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Sagas.Tests.Services;

public class SagaMethodInvokerTests
{
    public sealed record OrderPlaced(Guid OrderId);

    public sealed record PaymentReceived(Guid OrderId);

    public sealed record UnrelatedEvent(string Note);

    public sealed class DirectSaga : ISaga
    {
        public List<OrderPlaced> ReceivedOrders { get; } = [];

        [UsedImplicitly]
        public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken)
        {
            ReceivedOrders.Add(@event);
            return Task.CompletedTask;
        }
    }

    public sealed class WrappedSaga : ISaga
    {
        public List<IEvent<PaymentReceived>> Received { get; } = [];

        [UsedImplicitly]
        public Task HandleAsync(IEvent<PaymentReceived> @event, CancellationToken cancellationToken)
        {
            Received.Add(@event);
            return Task.CompletedTask;
        }
    }

    public sealed class PrivateHandlerSaga : ISaga
    {
        public bool Called { get; private set; }

        [UsedImplicitly]
        private Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    public sealed class MultiHandlerSaga : ISaga
    {
        [UsedImplicitly]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "Saga handler dispatch uses BindingFlags.Instance reflection; static methods would not be discovered.")]
        public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken) => Task.CompletedTask;

        [UsedImplicitly]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "Saga handler dispatch uses BindingFlags.Instance reflection; static methods would not be discovered.")]
        public Task HandleAsync(PaymentReceived @event, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class NoHandlerSaga : ISaga;

    [Fact]
    public void GetOrCreateRelevantEventTypes_ReturnsDeclaredEventTypes()
    {
        var invoker = new SagaMethodInvoker();

        var types = invoker.GetOrCreateRelevantEventTypes(new MultiHandlerSaga());

        Assert.Contains(typeof(OrderPlaced), types);
        Assert.Contains(typeof(PaymentReceived), types);
        Assert.Equal(2, types.Length);
    }

    [Fact]
    public void GetOrCreateRelevantEventTypes_UnwrapsIEventGenericArgument()
    {
        var invoker = new SagaMethodInvoker();

        var types = invoker.GetOrCreateRelevantEventTypes(new WrappedSaga());

        Assert.Equal([typeof(PaymentReceived)], types);
    }

    [Fact]
    public void GetOrCreateRelevantEventTypes_PicksUpPrivateHandleAsyncMethods()
    {
        var invoker = new SagaMethodInvoker();

        var types = invoker.GetOrCreateRelevantEventTypes(new PrivateHandlerSaga());

        Assert.Equal([typeof(OrderPlaced)], types);
    }

    [Fact]
    public void GetOrCreateRelevantEventTypes_ReturnsEmptyForSagaWithoutHandlers()
    {
        var invoker = new SagaMethodInvoker();

        var types = invoker.GetOrCreateRelevantEventTypes(new NoHandlerSaga());

        Assert.Empty(types);
    }

    [Fact]
    public void GetOrCreateRelevantEventTypes_ReturnsCachedReferenceOnSecondCall()
    {
        var invoker = new SagaMethodInvoker();
        var saga = new MultiHandlerSaga();

        var first = invoker.GetOrCreateRelevantEventTypes(saga);
        var second = invoker.GetOrCreateRelevantEventTypes(saga);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetOrCreateDelegate_ReturnsCompiledDelegateThatInvokesHandler()
    {
        var invoker = new SagaMethodInvoker();
        var saga = new DirectSaga();
        var orderEvent = new OrderPlaced(Guid.NewGuid());

        var handler = invoker.GetOrCreateDelegate(saga, typeof(OrderPlaced));
        await handler(saga, orderEvent, CancellationToken.None);

        Assert.Single(saga.ReceivedOrders);
        Assert.Equal(orderEvent, saga.ReceivedOrders[0]);
    }

    [Fact]
    public void GetOrCreateDelegate_ReturnsNoOpForUnknownEventType()
    {
        var invoker = new SagaMethodInvoker();
        var saga = new DirectSaga();

        var handler = invoker.GetOrCreateDelegate(saga, typeof(UnrelatedEvent));

        Assert.True(invoker.IsNoOp(handler));
    }

    [Fact]
    public void GetOrCreateDelegate_ReturnsCachedDelegateOnSecondCall()
    {
        var invoker = new SagaMethodInvoker();
        var saga = new DirectSaga();

        var first = invoker.GetOrCreateDelegate(saga, typeof(OrderPlaced));
        var second = invoker.GetOrCreateDelegate(saga, typeof(OrderPlaced));

        Assert.Same(first, second);
    }

    [Fact]
    public void IsNoOp_ReturnsFalseForRealDelegate()
    {
        var invoker = new SagaMethodInvoker();
        var saga = new DirectSaga();

        var handler = invoker.GetOrCreateDelegate(saga, typeof(OrderPlaced));

        Assert.False(invoker.IsNoOp(handler));
    }
}
