using Stratara.Contracts.Messages;
using Stratara.Abstractions.EventSourcing;
using Stratara.Contracts.Session;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.EventSourcing.Mapping;

namespace Stratara.Shared.Tests;

public class EventBundleMapperTests
{
    [Fact]
    public void MapToEventBundle_Maps_Entries_And_Serializes_SessionContext()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var entries = new List<EventStreamEntry>
        {
            new EventStreamEntry
            {
                Id = Guid.NewGuid(),
                StreamId = streamId,
                Version = 1,
                EventTypeName = typeof(EventBundleMapperTests).AssemblyQualifiedName!,
                AggregateTypeName = "Agg",
                DataJson = "{}",
                BucketId = 0,
                TenantId = tenantId,
                ActorTenantId = tenantId,
                ActorUserId = userId
            }
        };

        var session = new SessionContext(
            CorrelationId: Guid.NewGuid().ToString(),
            CausationId: null,
            ClientConnectionId: null,
            ActorTenantId: tenantId,
            ActorUserId: userId,
            TenantId: tenantId,
            UserId: null);

        // Act
        var bundle = entries.MapToEventBundle(session);

        // Assert
        Assert.IsType<EventBundle>(bundle);
        Assert.Single(bundle.Events);
        var ev = bundle.Events.Single();
        Assert.Equal(streamId, ev.StreamId);
        Assert.Equal(tenantId, ev.TenantId);
        Assert.Equal(tenantId, ev.ActorTenantId);
        Assert.Equal(userId, ev.ActorUserId);
        Assert.False(string.IsNullOrWhiteSpace(bundle.SessionContextJson));

        // It should be valid JSON containing known fields like CorrelationId
        Assert.Contains("\"CorrelationId\"", bundle.SessionContextJson);
        Assert.Contains(session.CorrelationId, bundle.SessionContextJson);
    }
}
