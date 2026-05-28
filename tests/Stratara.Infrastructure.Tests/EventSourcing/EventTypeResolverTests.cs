using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.Tests.EventSourcing;

public class EventTypeResolverTests
{
    private readonly EventTypeResolver _resolver = new();

    public sealed record TestEvent(string Name);
    public sealed record AnotherEvent(int Value);

    [Fact]
    public void Resolve_TypedEvent_ReturnsSameInstance()
    {
        var data = new TestEvent("test");
        var @event = new Event<TestEvent>(Guid.NewGuid(), 1, data, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result = _resolver.Resolve<TestEvent>(@event);

        Assert.Same(@event, result);
    }

    [Fact]
    public void ResolveDynamic_ReturnsWrappedEvent()
    {
        var data = new TestEvent("test");
        var @event = new Event<TestEvent>(Guid.NewGuid(), 1, data, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result = _resolver.ResolveDynamic(@event);

        Assert.IsType<Event<TestEvent>>(result);
    }

    [Fact]
    public void GetEventDataType_ReturnsCorrectType()
    {
        var data = new TestEvent("test");
        var @event = new Event<TestEvent>(Guid.NewGuid(), 1, data, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result = _resolver.GetEventDataType(@event);

        Assert.Equal(typeof(TestEvent), result);
    }

    [Fact]
    public void GetEventDataType_DifferentEventTypes_ReturnsDifferentTypes()
    {
        var event1 = new Event<TestEvent>(Guid.NewGuid(), 1, new TestEvent("test"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var event2 = new Event<AnotherEvent>(Guid.NewGuid(), 1, new AnotherEvent(42), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(typeof(TestEvent), _resolver.GetEventDataType(event1));
        Assert.Equal(typeof(AnotherEvent), _resolver.GetEventDataType(event2));
    }

    [Fact]
    public void ResolveDynamic_CachesResolver()
    {
        var data = new TestEvent("test");
        var event1 = new Event<TestEvent>(Guid.NewGuid(), 1, data, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var event2 = new Event<TestEvent>(Guid.NewGuid(), 2, new TestEvent("other"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result1 = _resolver.ResolveDynamic(event1);
        var result2 = _resolver.ResolveDynamic(event2);

        Assert.IsType<Event<TestEvent>>(result1);
        Assert.IsType<Event<TestEvent>>(result2);
    }
}
