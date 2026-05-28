using Stratara.Domain;
using Stratara.Domain.Multitenancy;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Shared.Tests.EventSourcing;

public class EventStreamTests
{
    private static IEvent CreateEvent<T>(T data, long version = 1) where T : notnull
    {
        return new Event<T>(
            Id: Guid.NewGuid(),
            Version: version,
            Data: data,
            StreamId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            UserId: Guid.NewGuid());
    }

    [Fact]
    public void Aggregate_EmptyEvents_ReturnsDefaultAggregate()
    {
        var tenant = EventStream.Aggregate<Tenant>([]);

        Assert.NotNull(tenant);
        Assert.Equal(Guid.Empty, tenant.Id);
        Assert.Equal(string.Empty, tenant.Name);
        Assert.Equal(string.Empty, tenant.DefaultLocale);
        Assert.True(tenant.IsActive);
    }

    [Fact]
    public void Aggregate_SingleEvent_AppliesCorrectly()
    {
        var tenantId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var events = new List<IEvent>
        {
            CreateEvent(new TenantCreated(tenantId, customerId, "Test", "en-US", true, createdAt))
        };

        var tenant = EventStream.Aggregate<Tenant>(events);

        Assert.Equal(tenantId, tenant.Id);
        Assert.Equal(customerId, tenant.CustomerId);
        Assert.Equal("Test", tenant.Name);
        Assert.Equal("en-US", tenant.DefaultLocale);
        Assert.True(tenant.IsActive);
        Assert.Equal(createdAt, tenant.CreatedAt);
    }

    [Fact]
    public void Aggregate_MultipleEvents_AppliesInOrder()
    {
        var tenantId = Guid.NewGuid();
        var events = new List<IEvent>
        {
            CreateEvent(new TenantCreated(tenantId, Guid.NewGuid(), "Original", "de-DE", true, DateTimeOffset.UtcNow), 1),
            CreateEvent(new TenantRenamed("Renamed", DateTimeOffset.UtcNow), 2),
            CreateEvent(new TenantDeactivated(DateTimeOffset.UtcNow), 3)
        };

        var tenant = EventStream.Aggregate<Tenant>(events);

        Assert.Equal(tenantId, tenant.Id);
        Assert.Equal("Renamed", tenant.Name);
        Assert.False(tenant.IsActive);
    }

    [Fact]
    public void Aggregate_NullEvents_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => EventStream.Aggregate<Tenant>(null!));
    }

    [Fact]
    public void Aggregate_UntypedOverload_CreatesCorrectType()
    {
        var tenantId = Guid.NewGuid();
        var events = new List<IEvent>
        {
            CreateEvent(new TenantCreated(tenantId, Guid.NewGuid(), "Untyped", "de-DE", true, DateTimeOffset.UtcNow))
        };

        var result = EventStream.Aggregate(typeof(Tenant), events);

        Assert.IsType<Tenant>(result);
        var tenant = (Tenant)result;
        Assert.Equal(tenantId, tenant.Id);
        Assert.Equal("Untyped", tenant.Name);
    }

    [Fact]
    public void ApplyEvents_WithMissingApplyMethod_IsNoOp()
    {
        var tenantId = Guid.NewGuid();
        var events = new List<IEvent>
        {
            CreateEvent(new TenantCreated(tenantId, Guid.NewGuid(), "Test", "de-DE", true, DateTimeOffset.UtcNow)),
            CreateEvent("some string that has no Apply method on Tenant")
        };

        var exception = Record.Exception(() => EventStream.Aggregate<Tenant>(events));

        Assert.Null(exception);
    }

    [Fact]
    public void ApplyEvents_MultipleSameType_AppliesAll()
    {
        var tenantId = Guid.NewGuid();
        var events = new List<IEvent>
        {
            CreateEvent(new TenantCreated(tenantId, Guid.NewGuid(), "First", "de-DE", true, DateTimeOffset.UtcNow), 1),
            CreateEvent(new TenantRenamed("Second", DateTimeOffset.UtcNow), 2),
            CreateEvent(new TenantRenamed("Third", DateTimeOffset.UtcNow), 3)
        };

        var tenant = EventStream.Aggregate<Tenant>(events);

        Assert.Equal("Third", tenant.Name);
    }

    [Fact]
    public void Aggregate_TenantLifecycle_FullRebuild()
    {
        var tenantId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var newCustomerId = Guid.NewGuid();
        var events = new List<IEvent>
        {
            CreateEvent(new TenantCreated(tenantId, customerId, "Startup", "de-DE", true, DateTimeOffset.UtcNow), 1),
            CreateEvent(new TenantRenamed("Startup GmbH", DateTimeOffset.UtcNow), 2),
            CreateEvent(new TenantDefaultLocaleChanged("en-US", DateTimeOffset.UtcNow), 3),
            CreateEvent(new TenantDeactivated(DateTimeOffset.UtcNow), 4),
            CreateEvent(new TenantActivated(DateTimeOffset.UtcNow), 5),
            CreateEvent(new TenantAssignedToCustomer(newCustomerId, DateTimeOffset.UtcNow), 6),
            CreateEvent(new TenantDeleted(DateTimeOffset.UtcNow), 7)
        };

        var tenant = EventStream.Aggregate<Tenant>(events);

        Assert.Equal(tenantId, tenant.Id);
        Assert.Equal("Startup GmbH", tenant.Name);
        Assert.Equal("en-US", tenant.DefaultLocale);
        Assert.True(tenant.IsActive);
        Assert.Equal(newCustomerId, tenant.CustomerId);
        Assert.True(tenant.IsDeleted);
    }

    [Fact]
    public void ApplyEvents_NullAggregate_ThrowsArgumentNull()
    {
        var events = new List<IEvent>
        {
            CreateEvent(new TenantCreated(Guid.NewGuid(), Guid.NewGuid(), "Test", "de-DE", true, DateTimeOffset.UtcNow))
        };

        Assert.Throws<ArgumentNullException>(() => ((object)null!).ApplyEvents(events));
    }

    [Fact]
    public void ApplyEvents_NullEvents_ThrowsArgumentNull()
    {
        var tenant = new Tenant();

        Assert.Throws<ArgumentNullException>(() => tenant.ApplyEvents(null!));
    }

    [Fact]
    public void Aggregate_CachesApplyDelegates()
    {
        var events1 = new List<IEvent>
        {
            CreateEvent(new TenantCreated(Guid.NewGuid(), Guid.NewGuid(), "First", "de-DE", true, DateTimeOffset.UtcNow))
        };
        var events2 = new List<IEvent>
        {
            CreateEvent(new TenantCreated(Guid.NewGuid(), Guid.NewGuid(), "Second", "de-DE", true, DateTimeOffset.UtcNow))
        };

        var tenant1 = EventStream.Aggregate<Tenant>(events1);
        var tenant2 = EventStream.Aggregate<Tenant>(events2);

        Assert.Equal("First", tenant1.Name);
        Assert.Equal("Second", tenant2.Name);
    }
}
