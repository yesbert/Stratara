using Stratara.Abstractions.Outbox;
using Stratara.Shared.Outbox.Mapping;

namespace Stratara.Shared.Tests;

public class OutboxEntryMapperTests
{
    private record Payload(string Name, int Value);

    [Fact]
    public void MapTo_Single_Deserializes_Payload()
    {
        // Arrange
        var payload = new Payload("A", 1);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var entry = new OutboxEntry { DataJson = json, DataTypeName = typeof(Payload).AssemblyQualifiedName!, BucketId = 0, Id = Guid.NewGuid() };

        // Act
        var mapped = entry.MapTo<Payload>();

        // Assert
        Assert.Equal(payload, mapped);
    }

    [Fact]
    public void MapTo_List_Deserializes_All()
    {
        // Arrange
        var payloads = new [] { new Payload("A", 1), new Payload("B", 2) };
        var entries = payloads.Select(p => new OutboxEntry
        {
            DataJson = System.Text.Json.JsonSerializer.Serialize(p),
            DataTypeName = typeof(Payload).AssemblyQualifiedName!,
            BucketId = 0,
            Id = Guid.NewGuid()
        }).ToList();

        // Act
        var mapped = entries.MapTo<Payload>();

        // Assert
        Assert.Equal(payloads, mapped);
    }

    [Fact]
    public void MapTo_Throws_On_Invalid_Json()
    {
        // Arrange
        var entry = new OutboxEntry { DataJson = "{ not-json }", DataTypeName = typeof(Payload).AssemblyQualifiedName!, BucketId = 0, Id = Guid.NewGuid() };

        // Act & Assert
        Assert.Throws<System.Text.Json.JsonException>(() => entry.MapTo<Payload>());
    }
}
