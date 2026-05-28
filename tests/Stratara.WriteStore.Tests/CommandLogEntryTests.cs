using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Entities;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

public class CommandLogEntryTests
{
    [Fact]
    public void RowVersion_Can_Be_Set_And_Retrieved()
    {
        var entry = new CommandLogEntry
        {
            CorrelationId = "corr-1",
            CommandJson = "{}",
            CommandTypeName = "TestCommand",
            BucketId = 0,
            TenantId = Guid.NewGuid(),
            ActorTenantId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid()
        };

        entry.RowVersion = 42u;

        Assert.Equal(42u, entry.RowVersion);
    }

    [Fact]
    public void All_Properties_Can_Be_Set()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var actorTenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var subjectUserId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;

        var entry = new CommandLogEntry
        {
            Id = id,
            CorrelationId = "corr-1",
            CausationId = "cause-1",
            CommandJson = "{\"key\":\"value\"}",
            CommandTypeName = "TestCommand",
            Timestamp = timestamp,
            BucketId = 5,
            TenantId = tenantId,
            UserId = subjectUserId,
            ActorTenantId = actorTenantId,
            ActorUserId = actorUserId,
            RowVersion = 10u
        };

        Assert.Equal(id, entry.Id);
        Assert.Equal("corr-1", entry.CorrelationId);
        Assert.Equal("cause-1", entry.CausationId);
        Assert.Equal("{\"key\":\"value\"}", entry.CommandJson);
        Assert.Equal("TestCommand", entry.CommandTypeName);
        Assert.Equal(timestamp, entry.Timestamp);
        Assert.Equal(5, entry.BucketId);
        Assert.Equal(tenantId, entry.TenantId);
        Assert.Equal(subjectUserId, entry.UserId);
        Assert.Equal(actorTenantId, entry.ActorTenantId);
        Assert.Equal(actorUserId, entry.ActorUserId);
        Assert.Equal(10u, entry.RowVersion);
    }
}
