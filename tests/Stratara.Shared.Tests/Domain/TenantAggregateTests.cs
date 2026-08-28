using System.Text.Json;
using Stratara.Abstractions.EventSourcing;
using Stratara.Domain;
using Stratara.Domain.Multitenancy;

namespace Stratara.Shared.Tests.Domain;

public class TenantAggregateTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var tenant = new Tenant();

        Assert.Equal(Guid.Empty, tenant.Id);
        Assert.Equal(Guid.Empty, tenant.CustomerId);
        Assert.Equal(string.Empty, tenant.Name);
        Assert.Equal(string.Empty, tenant.DefaultLocale);
        Assert.True(tenant.IsActive);
        Assert.Null(tenant.LastModifiedAt);
        Assert.Null(tenant.DeletedAt);
        Assert.False(tenant.IsDeleted);
    }

    [Fact]
    public void Apply_TenantCreated_SetsAllFields()
    {
        var tenant = new Tenant();
        var createdAt = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        tenant.Apply(new TenantCreated(tenantId, customerId, "My Tenant", "en-US", true, createdAt));

        Assert.Equal(tenantId, tenant.Id);
        Assert.Equal(customerId, tenant.CustomerId);
        Assert.Equal("My Tenant", tenant.Name);
        Assert.Equal("en-US", tenant.DefaultLocale);
        Assert.True(tenant.IsActive);
        Assert.Equal(createdAt, tenant.CreatedAt);
    }

    [Fact]
    public void Apply_TenantCreated_WithInactiveState()
    {
        var tenant = new Tenant();

        tenant.Apply(new TenantCreated(Guid.NewGuid(), Guid.NewGuid(), "Inactive", "de-DE", false, DateTimeOffset.UtcNow));

        Assert.False(tenant.IsActive);
    }

    [Fact]
    public void Apply_TenantDeleted_SetsDeletedAt()
    {
        var tenant = new Tenant();
        var deletedAt = DateTimeOffset.UtcNow;

        tenant.Apply(new TenantDeleted(deletedAt));

        Assert.Equal(deletedAt, tenant.DeletedAt);
        Assert.True(tenant.IsDeleted);
    }

    [Fact]
    public void IsDeleted_ReturnsFalse_WhenDeletedAtIsNull()
    {
        var tenant = new Tenant();

        Assert.False(tenant.IsDeleted);
    }

    [Fact]
    public void IsDeleted_ReturnsTrue_WhenDeletedAtIsSet()
    {
        var tenant = new Tenant();
        tenant.Apply(new TenantDeleted(DateTimeOffset.UtcNow));

        Assert.True(tenant.IsDeleted);
    }

    [Fact]
    public void Apply_TenantRenamed_UpdatesNameAndLastModifiedAt()
    {
        var tenant = new Tenant();
        var changedAt = DateTimeOffset.UtcNow;

        tenant.Apply(new TenantRenamed("New Name", changedAt));

        Assert.Equal("New Name", tenant.Name);
        Assert.Equal(changedAt, tenant.LastModifiedAt);
    }

    [Fact]
    public void Apply_TenantActivated_SetsIsActiveTrue()
    {
        var tenant = new Tenant { IsActive = false };

        var changedAt = DateTimeOffset.UtcNow;
        tenant.Apply(new TenantActivated(changedAt));

        Assert.True(tenant.IsActive);
        Assert.Equal(changedAt, tenant.LastModifiedAt);
    }

    [Fact]
    public void Apply_TenantDeactivated_SetsIsActiveFalse()
    {
        var tenant = new Tenant { IsActive = true };

        var changedAt = DateTimeOffset.UtcNow;
        tenant.Apply(new TenantDeactivated(changedAt));

        Assert.False(tenant.IsActive);
        Assert.Equal(changedAt, tenant.LastModifiedAt);
    }

    [Fact]
    public void Apply_TenantDefaultLocaleChanged_UpdatesDefaultLocale()
    {
        var tenant = new Tenant();
        var changedAt = DateTimeOffset.UtcNow;

        tenant.Apply(new TenantDefaultLocaleChanged("fr-FR", changedAt));

        Assert.Equal("fr-FR", tenant.DefaultLocale);
        Assert.Equal(changedAt, tenant.LastModifiedAt);
    }

    [Fact]
    public void Apply_TenantAssignedToCustomer_UpdatesCustomerId()
    {
        var tenant = new Tenant();
        var newCustomerId = Guid.NewGuid();
        var changedAt = DateTimeOffset.UtcNow;

        tenant.Apply(new TenantAssignedToCustomer(newCustomerId, changedAt));

        Assert.Equal(newCustomerId, tenant.CustomerId);
        Assert.Equal(changedAt, tenant.LastModifiedAt);
    }

    [Fact]
    public void MultipleEvents_Applied_InSequence()
    {
        var tenant = new Tenant();
        var tenantId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        tenant.Apply(new TenantCreated(tenantId, customerId, "Original", "de-DE", true, DateTimeOffset.UtcNow));
        tenant.Apply(new TenantRenamed("Renamed", DateTimeOffset.UtcNow));
        tenant.Apply(new TenantDeactivated(DateTimeOffset.UtcNow));

        Assert.Equal(tenantId, tenant.Id);
        Assert.Equal("Renamed", tenant.Name);
        Assert.False(tenant.IsActive);
    }

    [Fact]
    public void FullLifecycle_Created_Renamed_Deleted()
    {
        var tenant = new Tenant();
        var tenantId = Guid.NewGuid();

        tenant.Apply(new TenantCreated(tenantId, Guid.NewGuid(), "Lifecycle", "de-DE", true, DateTimeOffset.UtcNow));

        Assert.False(tenant.IsDeleted);

        tenant.Apply(new TenantRenamed("Lifecycle Updated", DateTimeOffset.UtcNow));

        Assert.Equal("Lifecycle Updated", tenant.Name);

        tenant.Apply(new TenantDeleted(DateTimeOffset.UtcNow));

        Assert.True(tenant.IsDeleted);
        Assert.Equal("Lifecycle Updated", tenant.Name);
    }

    [Fact]
    public void TenantCreated_DeclaresTheCreatedTenantAsItsOwner()
    {
        var tenantId = Guid.NewGuid();

        var creationEvent = Assert.IsAssignableFrom<IAggregateCreationEvent>(
            new TenantCreated(tenantId, Guid.NewGuid(), "Acme", "de-DE", true, DateTimeOffset.UtcNow));

        Assert.Equal(tenantId, creationEvent.TenantId);
    }

    [Fact]
    public void TenantCreated_SerializesWithoutAnOwningTenantField()
    {
        var json = JsonSerializer.Serialize(new TenantCreated(
            new Guid("11111111-1111-1111-1111-111111111111"),
            new Guid("22222222-2222-2222-2222-222222222222"),
            "Acme",
            "de-DE",
            true,
            new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero)));

        Assert.Equal(
            """{"Id":"11111111-1111-1111-1111-111111111111","CustomerId":"22222222-2222-2222-2222-222222222222","Name":"Acme","DefaultLocale":"de-DE","IsActive":true,"CreatedAt":"2026-08-27T10:00:00+00:00"}""",
            json);
    }
}
