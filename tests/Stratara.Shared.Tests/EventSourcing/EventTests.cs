using Stratara.Shared.EventSourcing;

namespace Stratara.Shared.Tests.EventSourcing;

public class EventTests
{
    private sealed record TestEvent(string Name, int Value);

    private sealed record AnotherEvent(Guid Id);

    [Fact]
    public void Properties_SetCorrectly()
    {
        var id = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var data = new TestEvent("test", 42);

        var sut = new Event<TestEvent>(id, 5, data, streamId, tenantId, userId, "TestAggregate");

        Assert.Equal(id, sut.Id);
        Assert.Equal(5, sut.Version);
        Assert.Equal(data, sut.Data);
        Assert.Equal(streamId, sut.StreamId);
        Assert.Equal(tenantId, sut.TenantId);
        Assert.Equal(userId, sut.UserId);
        Assert.Equal("TestAggregate", sut.AggregateTypeName);
    }

    [Fact]
    public void EventTypeName_ReturnsQualifiedTypeName()
    {
        var data = new TestEvent("test", 1);
        var sut = new Event<TestEvent>(Guid.NewGuid(), 1, data, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var typeName = sut.EventTypeName;

        Assert.Contains("TestEvent", typeName);
        Assert.NotEmpty(typeName);
    }

    [Fact]
    public void EventTypeName_DiffersForDifferentEventTypes()
    {
        var event1 = new Event<TestEvent>(Guid.NewGuid(), 1, new TestEvent("a", 1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var event2 = new Event<AnotherEvent>(Guid.NewGuid(), 1, new AnotherEvent(Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.NotEqual(event1.EventTypeName, event2.EventTypeName);
    }

    [Fact]
    public void IEvent_Data_ReturnsBoxedData()
    {
        var data = new TestEvent("test", 42);
        Stratara.Abstractions.EventSourcing.IEvent sut = new Event<TestEvent>(Guid.NewGuid(), 1, data, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(data, sut.Data);
    }

    [Fact]
    public void AggregateTypeName_DefaultsToUnknown()
    {
        var sut = new Event<TestEvent>(Guid.NewGuid(), 1, new TestEvent("test", 1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal("Unknown", sut.AggregateTypeName);
    }
}
