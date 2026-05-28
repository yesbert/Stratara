using JetBrains.Annotations;
using Stratara.Domain;
using Stratara.Projections.Abstractions;
using Stratara.Projections.Multitenancy.Models;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Projections.Multitenancy;

/// <summary>
/// Built-in projection that materialises the <c>Tenant</c> aggregate (from <c>Stratara.Domain</c>) into the
/// <see cref="TenantView"/> read store. Handles the full tenant lifecycle: creation, deletion, rename,
/// activation, locale change, and customer reassignment.
/// </summary>
/// <remarks>
/// All <c>HandleAsync</c> methods are private and discovered via reflection by
/// <see cref="IProjectionMethodInvoker"/>. Each handler is idempotent against re-delivery — it checks for
/// existing rows before insert and silently skips updates against deleted views.
/// </remarks>
[UsedImplicitly]
public sealed class TenantProjection(IProjectionsUnitOfWork unitOfWork) : IProjection
{
    [UsedImplicitly]
    private async Task HandleAsync(IEvent<TenantCreated> @event, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var tenantRepository = unitOfWork.CreateTenantRepository(transaction);

        if (await tenantRepository.ExistsAsync(@event.Data.Id, cancellationToken))
        {
            return;
        }

        var data = @event.Data;
        var tenant = new TenantView
        {
            Id = data.Id,
            CustomerId = data.CustomerId,
            Name = data.Name,
            DefaultLocale = data.DefaultLocale,
            IsActive = data.IsActive,
            CreatedAt = data.CreatedAt,
            SourceVersion = @event.Version
        };

        await tenantRepository.AddAsync(tenant, cancellationToken);
        await transaction.SaveChangesAsync(cancellationToken);
    }

    [UsedImplicitly]
    private async Task HandleAsync(IEvent<TenantDeleted> @event, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateTenantRepository(transaction);

        var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        await repository.DeleteAsync(@event.StreamId, cancellationToken);
        await transaction.SaveChangesAsync(cancellationToken);
    }

    [UsedImplicitly]
    private async Task HandleAsync(IEvent<CustomerTenantsDeleted> @event, CancellationToken cancellationToken)
    {
        var tenantIds = @event.Data.TenantIds;
        if (tenantIds is null || tenantIds.Count == 0)
        {
            return;
        }

        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateTenantRepository(transaction);

        foreach (var tenantId in tenantIds)
        {
            await repository.DeleteAsync(tenantId, cancellationToken);
        }

        await transaction.SaveChangesAsync(cancellationToken);
    }

    [UsedImplicitly]
    private async Task HandleAsync(IEvent<TenantRenamed> @event, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateTenantRepository(transaction);

        var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        var data = @event.Data;
        tenant.Name = data.Name;
        tenant.LastModifiedAt = data.ChangedAt;
        tenant.SourceVersion = @event.Version;

        repository.Update(tenant);
        await transaction.SaveChangesAsync(cancellationToken);
    }

    [UsedImplicitly]
    private async Task HandleAsync(IEvent<TenantActivated> @event, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateTenantRepository(transaction);

        var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        tenant.IsActive = true;
        tenant.LastModifiedAt = @event.Data.ChangedAt;
        tenant.SourceVersion = @event.Version;

        repository.Update(tenant);
        await transaction.SaveChangesAsync(cancellationToken);
    }

    [UsedImplicitly]
    private async Task HandleAsync(IEvent<TenantDeactivated> @event, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateTenantRepository(transaction);

        var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        tenant.IsActive = false;
        tenant.LastModifiedAt = @event.Data.ChangedAt;
        tenant.SourceVersion = @event.Version;

        repository.Update(tenant);
        await transaction.SaveChangesAsync(cancellationToken);
    }

    [UsedImplicitly]
    private async Task HandleAsync(IEvent<TenantDefaultLocaleChanged> @event, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateTenantRepository(transaction);

        var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        tenant.DefaultLocale = @event.Data.DefaultLocale;
        tenant.LastModifiedAt = @event.Data.ChangedAt;
        tenant.SourceVersion = @event.Version;

        repository.Update(tenant);
        await transaction.SaveChangesAsync(cancellationToken);
    }

    [UsedImplicitly]
    private async Task HandleAsync(IEvent<TenantAssignedToCustomer> @event, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateTenantRepository(transaction);

        var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        tenant.CustomerId = @event.Data.CustomerId;
        tenant.LastModifiedAt = @event.Data.ChangedAt;
        tenant.SourceVersion = @event.Version;

        repository.Update(tenant);
        await transaction.SaveChangesAsync(cancellationToken);
    }
}
