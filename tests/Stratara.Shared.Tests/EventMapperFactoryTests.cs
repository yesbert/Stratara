using System.Text.Json.Nodes;
using Moq;
using Stratara.Contracts.Messages;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.EventSourcing.Mapping;

namespace Stratara.Shared.Tests;

public class EventMapperFactoryTests
{
    private record UserCreated(string Name);

    private sealed class DelegateUpcaster(string source, string target, Func<JsonNode, JsonNode> transform) : IEventUpcaster
    {
        public string SourceEventTypeName => source;
        public string TargetEventTypeName => target;
        public JsonNode Upcast(JsonNode payload) => transform(payload);
    }

    private static TrustedTypeResolver CreateResolverWithUserCreated()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(UserCreated));
        return resolver;
    }

    private static EventUpcasterPipeline EmptyPipeline() => new([]);

    private static EventStreamEntry CreateEntry(Guid tenantId, Guid userId, Guid streamId, long version, object data)
    {
        return new EventStreamEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ActorTenantId = tenantId,
            ActorUserId = userId,
            StreamId = streamId,
            Version = version,
            EventTypeName = data.GetType().AssemblyQualifiedName!,
            AggregateTypeName = "Agg",
            DataJson = "{}",
            BucketId = 0
        };
    }

    private static EventMessage CreateMessage(Guid tenantId, Guid userId, Guid streamId, long version, object data)
    {
        return new EventMessage(
            Id: Guid.NewGuid(),
            Version: version,
            DataJson: "{}",
            StreamId: streamId,
            EventTypeName: data.GetType().AssemblyQualifiedName!,
            AggregateTypeName: "Agg",
            ActorTenantId: tenantId,
            ActorUserId: userId,
            TenantId: tenantId,
            UserId: null
        );
    }

    [Fact]
    public async Task MapToEvents_From_EventStreamEntry_Uses_Deserializer_And_Constructs_Event()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var data = new UserCreated("A");
        var entry = CreateEntry(tenantId, userId, streamId, 1, data);

        var serializer = new Mock<ISecureJsonSerializer>();
        serializer.Setup(s => s.DeserializeAsync("{}", typeof(UserCreated), tenantId, (Guid?)null, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(data);

        var sut = new EventMapperFactory(serializer.Object, CreateResolverWithUserCreated(), EmptyPipeline());

        // Act
        var events = await sut.MapToEventsAsync(new[] { entry });
        var ev = Assert.IsType<IEvent>(events.Single(), exactMatch: false);

        // Assert
        Assert.Equal(streamId, ev.StreamId);
        Assert.Equal(tenantId, ev.TenantId);
        Assert.Equal(userId, ev.UserId);
        Assert.Equal(1, ev.Version);
        Assert.Equal("Agg", ev.AggregateTypeName);
        Assert.Equal(typeof(UserCreated).AssemblyQualifiedName, ev.EventTypeName);
        Assert.IsType<Event<UserCreated>>(ev);
        Assert.Equal("A", ((Event<UserCreated>)ev).Data.Name);
    }

    [Fact]
    public async Task MapToEvents_From_EventMessage_Uses_Deserializer_And_Constructs_Event()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var data = new UserCreated("A");
        var message = CreateMessage(tenantId, userId, streamId, 2, data);

        var serializer = new Mock<ISecureJsonSerializer>();
        serializer.Setup(s => s.DeserializeAsync("{}", typeof(UserCreated), tenantId, (Guid?)null, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(data);

        var sut = new EventMapperFactory(serializer.Object, CreateResolverWithUserCreated(), EmptyPipeline());

        // Act
        var events = await sut.MapToEventsAsync(new[] { message });
        var ev = events.Single();

        // Assert
        Assert.Equal(2, ev.Version);
        Assert.IsType<Event<UserCreated>>(ev);
        Assert.Equal("A", ((Event<UserCreated>)ev).Data.Name);
    }

    [Fact]
    public async Task MapToEvents_Throws_When_Type_Not_Found()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var entry = new EventStreamEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ActorTenantId = tenantId,
            ActorUserId = userId,
            StreamId = streamId,
            Version = 1,
            EventTypeName = "Stratara.Shared.Tests.DoesNotExist, Stratara.Shared.Tests",
            AggregateTypeName = "Agg",
            DataJson = "{}",
            BucketId = 0
        };

        var serializer = new Mock<ISecureJsonSerializer>(MockBehavior.Strict);
        var sut = new EventMapperFactory(serializer.Object, CreateResolverWithUserCreated(), EmptyPipeline());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.MapToEventsAsync(new[] { entry }));
    }

    [Fact]
    public async Task MapToEvents_Resolves_EventType_When_AssemblyVersion_Differs()
    {
        // Arrange — simulates an event stored by a previous/future build (e.g. prod DB restored locally).
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var data = new UserCreated("A");

        var typeName = typeof(UserCreated).FullName!;
        var assemblyName = typeof(UserCreated).Assembly.GetName().Name!;
        var mutatedEventTypeName = $"{typeName}, {assemblyName}, Version=99.99.99.99, Culture=neutral, PublicKeyToken=null";

        var entry = new EventStreamEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ActorTenantId = tenantId,
            ActorUserId = userId,
            StreamId = streamId,
            Version = 1,
            EventTypeName = mutatedEventTypeName,
            AggregateTypeName = "Agg",
            DataJson = "{}",
            BucketId = 0
        };

        var serializer = new Mock<ISecureJsonSerializer>();
        serializer.Setup(s => s.DeserializeAsync("{}", typeof(UserCreated), tenantId, (Guid?)null, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(data);

        var sut = new EventMapperFactory(serializer.Object, CreateResolverWithUserCreated(), EmptyPipeline());

        // Act
        var events = await sut.MapToEventsAsync(new[] { entry });

        // Assert
        var ev = events.Single();
        Assert.IsType<Event<UserCreated>>(ev);
        Assert.Equal("A", ((Event<UserCreated>)ev).Data.Name);
    }

    [Fact]
    public async Task MapToEvents_Throws_When_Deserializer_Returns_Null()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var dataType = typeof(UserCreated);
        var entry = new EventStreamEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ActorTenantId = tenantId,
            ActorUserId = userId,
            StreamId = streamId,
            Version = 1,
            EventTypeName = dataType.AssemblyQualifiedName!,
            AggregateTypeName = "Agg",
            DataJson = "{}",
            BucketId = 0
        };

        var serializer = new Mock<ISecureJsonSerializer>();
        serializer.Setup(s => s.DeserializeAsync("{}", dataType, tenantId, (Guid?)null, It.IsAny<CancellationToken>()))
                  .ReturnsAsync((object?)null);

        var sut = new EventMapperFactory(serializer.Object, CreateResolverWithUserCreated(), EmptyPipeline());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.MapToEventsAsync(new[] { entry }));
    }

    [Fact]
    public async Task MapToEvents_Applies_Upcaster_Renaming_Type_And_Payload_Field()
    {
        // Arrange — the stored event is an old, since-removed type whose payload used a different
        // field name. The upcaster renames both the type (to the current UserCreated) and the field.
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        const string oldTypeName = "Stratara.Shared.Tests.UserRegistered, Stratara.Shared.Tests";

        var entry = new EventStreamEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ActorTenantId = tenantId,
            ActorUserId = userId,
            StreamId = streamId,
            Version = 1,
            EventTypeName = oldTypeName,
            AggregateTypeName = "Agg",
            DataJson = "{\"fullName\":\"A\"}",
            BucketId = 0
        };

        var upcaster = new DelegateUpcaster(oldTypeName, typeof(UserCreated).AssemblyQualifiedName!, node =>
        {
            var obj = node.AsObject();
            var name = (string?)obj["fullName"];
            obj.Remove("fullName");
            obj["name"] = name;
            return obj;
        });
        var pipeline = new EventUpcasterPipeline([upcaster]);

        var serializer = new Mock<ISecureJsonSerializer>();
        serializer.Setup(s => s.DeserializeAsync(
                      It.Is<string>(j => j.Contains("\"name\"") && !j.Contains("fullName")),
                      typeof(UserCreated), tenantId, (Guid?)null, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new UserCreated("A"));

        var sut = new EventMapperFactory(serializer.Object, CreateResolverWithUserCreated(), pipeline);

        // Act
        var events = await sut.MapToEventsAsync(new[] { entry });

        // Assert
        var ev = events.Single();
        Assert.IsType<Event<UserCreated>>(ev);
        Assert.Equal("A", ((Event<UserCreated>)ev).Data.Name);
    }
}
