using Moq;
using Stratara.Domain;
using Stratara.Projections.Multitenancy;
using Stratara.Projections.Multitenancy.Models;
using Stratara.Projections.Tests.Helpers;

namespace Stratara.Projections.Tests.Multitenancy;

public class TenantProjectionTests : ProjectionTestBase
{
    private readonly TenantProjection _projection;

    public TenantProjectionTests()
    {
        _projection = new TenantProjection(UnitOfWorkMock.Object);
    }

    #region TenantCreated

    [Fact]
    public async Task HandleAsync_TenantCreated_CreatesViewWithCorrectFields()
    {
        var tenantId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var data = new TenantCreated(tenantId, customerId, "Test Tenant", "en-US", true, createdAt);
        var @event = EventFactory.Create(data, streamId: tenantId);

        TenantRepositoryMock.Setup(r => r.ExistsAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        TenantView? capturedTenant = null;
        TenantRepositoryMock.Setup(r => r.AddAsync(It.IsAny<TenantView>(), It.IsAny<CancellationToken>()))
            .Callback<TenantView, CancellationToken>((t, _) => capturedTenant = t)
            .ReturnsAsync(tenantId);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        Assert.NotNull(capturedTenant);
        Assert.Equal(tenantId, capturedTenant.Id);
        Assert.Equal(customerId, capturedTenant.CustomerId);
        Assert.Equal("Test Tenant", capturedTenant.Name);
        Assert.Equal("en-US", capturedTenant.DefaultLocale);
        Assert.True(capturedTenant.IsActive);
        Assert.Equal(createdAt, capturedTenant.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_TenantCreated_SetsSourceVersion()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantCreated(tenantId, Guid.NewGuid(), "Tenant", "de-DE", true, DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId, version: 42);

        TenantRepositoryMock.Setup(r => r.ExistsAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        TenantView? capturedTenant = null;
        TenantRepositoryMock.Setup(r => r.AddAsync(It.IsAny<TenantView>(), It.IsAny<CancellationToken>()))
            .Callback<TenantView, CancellationToken>((t, _) => capturedTenant = t)
            .ReturnsAsync(tenantId);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        Assert.NotNull(capturedTenant);
        Assert.Equal(42, capturedTenant.SourceVersion);
    }

    [Fact]
    public async Task HandleAsync_TenantCreated_Idempotent_WhenAlreadyExists()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantCreated(tenantId, Guid.NewGuid(), "Tenant", "de-DE", true, DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId);

        TenantRepositoryMock.Setup(r => r.ExistsAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.AddAsync(It.IsAny<TenantView>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_TenantCreated_CallsSaveChangesAsync()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantCreated(tenantId, Guid.NewGuid(), "Tenant", "de-DE", true, DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId);

        TenantRepositoryMock.Setup(r => r.ExistsAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TenantRepositoryMock.Setup(r => r.AddAsync(It.IsAny<TenantView>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantId);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TransactionMock.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_TenantCreated_Idempotent_DoesNotCallSaveChanges()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantCreated(tenantId, Guid.NewGuid(), "Tenant", "de-DE", true, DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId);

        TenantRepositoryMock.Setup(r => r.ExistsAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TransactionMock.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region TenantDeleted

    [Fact]
    public async Task HandleAsync_TenantDeleted_DeletesTenant()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantDeleted(DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId);

        var existingTenant = new TenantView { Id = tenantId, Name = "Existing" };
        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.DeleteAsync(tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_TenantDeleted_NoOp_WhenTenantNotFound()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantDeleted(DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId);

        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantView?)null);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_TenantDeleted_CallsSaveChangesAsync()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantDeleted(DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId);

        var existingTenant = new TenantView { Id = tenantId, Name = "Existing" };
        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TransactionMock.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region CustomerTenantsDeleted

    [Fact]
    public async Task HandleAsync_CustomerTenantsDeleted_DeletesAllTenants()
    {
        var customerId = Guid.NewGuid();
        var tenantIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var data = new CustomerTenantsDeleted(customerId, tenantIds, DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        foreach (var tenantId in tenantIds)
        {
            TenantRepositoryMock.Verify(r => r.DeleteAsync(tenantId, It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task HandleAsync_CustomerTenantsDeleted_EmptyList_NoOp()
    {
        var data = new CustomerTenantsDeleted(Guid.NewGuid(), [], DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        UnitOfWorkMock.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_CustomerTenantsDeleted_CallsSaveChangesAsync()
    {
        var tenantIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var data = new CustomerTenantsDeleted(Guid.NewGuid(), tenantIds, DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TransactionMock.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_CustomerTenantsDeleted_NullTenantIds_NoOp()
    {
        var data = new CustomerTenantsDeleted(Guid.NewGuid(), null!, DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        UnitOfWorkMock.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region TenantRenamed

    [Fact]
    public async Task HandleAsync_TenantRenamed_UpdatesNameAndLastModifiedAt()
    {
        var tenantId = Guid.NewGuid();
        var changedAt = DateTimeOffset.UtcNow;
        var data = new TenantRenamed("New Name", changedAt);
        var @event = EventFactory.Create(data, streamId: tenantId);

        var existingTenant = new TenantView { Id = tenantId, Name = "Old Name" };
        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        Assert.Equal("New Name", existingTenant.Name);
        Assert.Equal(changedAt, existingTenant.LastModifiedAt);
        TenantRepositoryMock.Verify(r => r.Update(existingTenant), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_TenantRenamed_SetsSourceVersion()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantRenamed("Renamed", DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId, version: 5);

        var existingTenant = new TenantView { Id = tenantId, Name = "Old", SourceVersion = 1 };
        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        Assert.Equal(5, existingTenant.SourceVersion);
    }

    [Fact]
    public async Task HandleAsync_TenantRenamed_NoOp_WhenTenantNotFound()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantRenamed("New Name", DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId);

        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantView?)null);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.Update(It.IsAny<TenantView>()), Times.Never);
    }

    #endregion

    #region TenantActivated

    [Fact]
    public async Task HandleAsync_TenantActivated_SetsIsActiveTrue()
    {
        var tenantId = Guid.NewGuid();
        var changedAt = DateTimeOffset.UtcNow;
        var data = new TenantActivated(changedAt);
        var @event = EventFactory.Create(data, streamId: tenantId);

        var existingTenant = new TenantView { Id = tenantId, IsActive = false };
        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        Assert.True(existingTenant.IsActive);
        Assert.Equal(changedAt, existingTenant.LastModifiedAt);
        TenantRepositoryMock.Verify(r => r.Update(existingTenant), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_TenantActivated_NoOp_WhenNotFound()
    {
        var data = new TenantActivated(DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: Guid.NewGuid());

        TenantRepositoryMock.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantView?)null);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.Update(It.IsAny<TenantView>()), Times.Never);
    }

    #endregion

    #region TenantDeactivated

    [Fact]
    public async Task HandleAsync_TenantDeactivated_SetsIsActiveFalse()
    {
        var tenantId = Guid.NewGuid();
        var changedAt = DateTimeOffset.UtcNow;
        var data = new TenantDeactivated(changedAt);
        var @event = EventFactory.Create(data, streamId: tenantId);

        var existingTenant = new TenantView { Id = tenantId, IsActive = true };
        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        Assert.False(existingTenant.IsActive);
        Assert.Equal(changedAt, existingTenant.LastModifiedAt);
        TenantRepositoryMock.Verify(r => r.Update(existingTenant), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_TenantDeactivated_NoOp_WhenNotFound()
    {
        var data = new TenantDeactivated(DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: Guid.NewGuid());

        TenantRepositoryMock.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantView?)null);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.Update(It.IsAny<TenantView>()), Times.Never);
    }

    #endregion

    #region TenantDefaultLocaleChanged

    [Fact]
    public async Task HandleAsync_TenantDefaultLocaleChanged_UpdatesLocale()
    {
        var tenantId = Guid.NewGuid();
        var changedAt = DateTimeOffset.UtcNow;
        var data = new TenantDefaultLocaleChanged("fr-FR", changedAt);
        var @event = EventFactory.Create(data, streamId: tenantId, version: 3);

        var existingTenant = new TenantView { Id = tenantId, DefaultLocale = "de-DE" };
        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        Assert.Equal("fr-FR", existingTenant.DefaultLocale);
        Assert.Equal(changedAt, existingTenant.LastModifiedAt);
        Assert.Equal(3, existingTenant.SourceVersion);
        TenantRepositoryMock.Verify(r => r.Update(existingTenant), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_TenantDefaultLocaleChanged_NoOp_WhenNotFound()
    {
        var data = new TenantDefaultLocaleChanged("en-US", DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: Guid.NewGuid());

        TenantRepositoryMock.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantView?)null);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.Update(It.IsAny<TenantView>()), Times.Never);
    }

    #endregion

    #region TenantAssignedToCustomer

    [Fact]
    public async Task HandleAsync_TenantAssignedToCustomer_UpdatesCustomerId()
    {
        var tenantId = Guid.NewGuid();
        var newCustomerId = Guid.NewGuid();
        var changedAt = DateTimeOffset.UtcNow;
        var data = new TenantAssignedToCustomer(newCustomerId, changedAt);
        var @event = EventFactory.Create(data, streamId: tenantId, version: 7);

        var existingTenant = new TenantView { Id = tenantId, CustomerId = Guid.NewGuid() };
        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        Assert.Equal(newCustomerId, existingTenant.CustomerId);
        Assert.Equal(changedAt, existingTenant.LastModifiedAt);
        Assert.Equal(7, existingTenant.SourceVersion);
        TenantRepositoryMock.Verify(r => r.Update(existingTenant), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_TenantAssignedToCustomer_NoOp_WhenNotFound()
    {
        var data = new TenantAssignedToCustomer(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: Guid.NewGuid());

        TenantRepositoryMock.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantView?)null);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TenantRepositoryMock.Verify(r => r.Update(It.IsAny<TenantView>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_TenantAssignedToCustomer_CallsSaveChangesAsync()
    {
        var tenantId = Guid.NewGuid();
        var data = new TenantAssignedToCustomer(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var @event = EventFactory.Create(data, streamId: tenantId);

        var existingTenant = new TenantView { Id = tenantId };
        TenantRepositoryMock.Setup(r => r.GetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        await ProjectionTestHelper.HandleAsync(_projection, @event);

        TransactionMock.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}
