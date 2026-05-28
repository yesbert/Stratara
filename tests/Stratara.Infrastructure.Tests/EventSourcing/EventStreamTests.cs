using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.Tests.EventSourcing;

public class EventStreamTests
{
    public sealed record NameChanged(string Name);
    public sealed record DescriptionChanged(string Description);
    public sealed record UnknownEvent(string Data);

    public class TestAggregate
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";

        public void Apply(NameChanged e) => Name = e.Name;
        public void Apply(DescriptionChanged e) => Description = e.Description;
    }

    public class AggregateWithWrappedApply
    {
        public string Name { get; set; } = "";

        public void Apply(IEvent<NameChanged> e) => Name = e.Data.Name;
    }

    [Fact]
    public void Aggregate_WithEvents_AppliesAll()
    {
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var events = new List<IEvent>
        {
            new Event<NameChanged>(Guid.NewGuid(), 1, new NameChanged("Test"), streamId, tenantId, userId),
            new Event<DescriptionChanged>(Guid.NewGuid(), 2, new DescriptionChanged("Desc"), streamId, tenantId, userId)
        };

        var result = EventStream.Aggregate<TestAggregate>(events);

        Assert.Equal("Test", result.Name);
        Assert.Equal("Desc", result.Description);
    }

    [Fact]
    public void Aggregate_WithEmptyEvents_ReturnsDefaultAggregate()
    {
        var result = EventStream.Aggregate<TestAggregate>([]);

        Assert.Equal("", result.Name);
        Assert.Equal("", result.Description);
    }

    [Fact]
    public void Aggregate_UnknownEventType_DoesNotThrow()
    {
        var events = new List<IEvent>
        {
            new Event<UnknownEvent>(Guid.NewGuid(), 1, new UnknownEvent("data"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        };

        var result = EventStream.Aggregate<TestAggregate>(events);

        Assert.Equal("", result.Name);
    }

    [Fact]
    public void Aggregate_ByType_AppliesEvents()
    {
        var events = new List<IEvent>
        {
            new Event<NameChanged>(Guid.NewGuid(), 1, new NameChanged("TypeTest"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        };

        var result = EventStream.Aggregate(typeof(TestAggregate), events);

        Assert.IsType<TestAggregate>(result);
        Assert.Equal("TypeTest", ((TestAggregate)result).Name);
    }

    [Fact]
    public void ApplyEvents_OnExistingAggregate_ModifiesState()
    {
        var aggregate = new TestAggregate { Name = "Original" };
        var events = new List<IEvent>
        {
            new Event<NameChanged>(Guid.NewGuid(), 1, new NameChanged("Updated"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        };

        aggregate.ApplyEvents(events);

        Assert.Equal("Updated", aggregate.Name);
    }

    [Fact]
    public void ApplyEvents_MultipleEvents_AppliesInOrder()
    {
        var aggregate = new TestAggregate();
        var events = new List<IEvent>
        {
            new Event<NameChanged>(Guid.NewGuid(), 1, new NameChanged("First"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new Event<NameChanged>(Guid.NewGuid(), 2, new NameChanged("Second"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new Event<NameChanged>(Guid.NewGuid(), 3, new NameChanged("Third"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        };

        aggregate.ApplyEvents(events);

        Assert.Equal("Third", aggregate.Name);
    }

    [Fact]
    public void Aggregate_WithWrappedEventApply_UsesIEventOverload()
    {
        var events = new List<IEvent>
        {
            new Event<NameChanged>(Guid.NewGuid(), 1, new NameChanged("Wrapped"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        };

        var result = EventStream.Aggregate<AggregateWithWrappedApply>(events);

        Assert.Equal("Wrapped", result.Name);
    }
}
