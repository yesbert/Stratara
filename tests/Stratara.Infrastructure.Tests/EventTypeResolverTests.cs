using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Infrastructure.Tests;

public class EventTypeResolverTests
{
    [Fact]
    public void Resolve_Generic_Returns_Typed_Interface_When_Already_Typed()
    {
        // Arrange
        var evt = new EventWrapper(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.Empty, Guid.Empty, new MyEvent("A"));
        var resolver = new EventTypeResolver();

        // Act
        var typed = resolver.Resolve<MyEvent>(evt);

        // Assert
        Assert.Equal("A", typed.Data.Name);
    }

    [Fact]
    public void ResolveDynamic_Returns_Typed_Interface_Dynamically()
    {
        // Arrange
        IEvent evt = new EventWrapper(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.Empty, Guid.Empty, new MyEvent("B"));
        var resolver = new EventTypeResolver();

        // Act
        var result = resolver.ResolveDynamic(evt);

        // Assert
        Assert.IsType<IEvent>(result, exactMatch: false);
        var typed = Assert.IsType<IEvent<MyEvent>>(result, exactMatch: false);
        Assert.Equal("B", typed.Data.Name);
    }

    [Fact]
    public void GetEventDataType_Returns_Concrete_Data_Type()
    {
        // Arrange
        IEvent evt = new EventWrapper(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.Empty, Guid.Empty, new MyEvent("C"));
        var resolver = new EventTypeResolver();

        // Act
        var type = resolver.GetEventDataType(evt);

        // Assert
        Assert.Equal(typeof(MyEvent), type);
    }

    private sealed record MyEvent(string Name);

    private sealed class EventWrapper(Guid id, long version, Guid streamId, Guid tenantId, Guid userId, MyEvent data) : IEvent<MyEvent>
    {
        public MyEvent TypedData { get; } = data;
        public Guid Id { get; } = id;
        public long Version { get; } = version;
        public object Data => TypedData;
        public Guid StreamId { get; } = streamId;
        public string EventTypeName { get; } = typeof(MyEvent).FullName!;
        public string AggregateTypeName { get; } = "Agg";
        public Guid TenantId { get; } = tenantId;
        public Guid UserId { get; } = userId;
        MyEvent IEvent<MyEvent>.Data => TypedData;
    }
}