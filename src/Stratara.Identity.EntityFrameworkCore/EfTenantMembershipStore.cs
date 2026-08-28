using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.Multitenancy;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="ITenantMembershipStore"/> against any DbContext whose
/// model includes the identity-directory tables (via
/// <see cref="IdentityDirectoryDbContext{TContext}"/> or
/// <see cref="IdentityDirectoryModelBuilderExtensions.ApplyIdentityDirectoryModel"/>).
/// </summary>
internal sealed class EfTenantMembershipStore<TContext>(IDirectoryContextSource<TContext> contextSource)
    : ITenantMembershipStore
    where TContext : DbContext
{
    public async Task<IReadOnlyList<TenantMembership>> GetMembershipsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);

        var entries = await lease.Context.Set<TenantMembershipEntry>()
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .ToListAsync(cancellationToken);

        return entries.Select(ToContract).ToList();
    }

    public async Task<TenantMembership?> GetMembershipAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);

        var entry = await lease.Context.Set<TenantMembershipEntry>()
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.UserId == userId && e.TenantId == tenantId, cancellationToken);

        return entry is null ? null : ToContract(entry);
    }

    public async Task<IReadOnlyList<TenantMembership>> GetMembersAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);

        var entries = await lease.Context.Set<TenantMembershipEntry>()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        return entries.Select(ToContract).ToList();
    }

    public async Task SetMembershipAsync(
        TenantMembership membership, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);
        var context = lease.Context;

        var existing = await context.Set<TenantMembershipEntry>()
            .SingleOrDefaultAsync(
                e => e.UserId == membership.UserId && e.TenantId == membership.TenantId,
                cancellationToken);

        if (existing is null)
        {
            context.Set<TenantMembershipEntry>().Add(new TenantMembershipEntry
            {
                UserId = membership.UserId,
                TenantId = membership.TenantId,
                Roles = membership.Roles.ToList(),
                Status = membership.Status,
            });
        }
        else
        {
            existing.Roles = membership.Roles.ToList();
            existing.Status = membership.Status;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMembershipAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);
        var context = lease.Context;

        await context.Set<TenantMembershipEntry>()
            .Where(e => e.UserId == userId && e.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Set<ActiveTenantEntry>()
            .Where(e => e.UserId == userId && e.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task RemoveAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);
        var context = lease.Context;

        await context.Set<TenantMembershipEntry>()
            .Where(e => e.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Set<ActiveTenantEntry>()
            .Where(e => e.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task RemoveAllMembersAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);
        var context = lease.Context;

        await context.Set<TenantMembershipEntry>()
            .Where(e => e.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Set<ActiveTenantEntry>()
            .Where(e => e.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<Guid?> GetActiveTenantAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);

        var selection = await lease.Context.Set<ActiveTenantEntry>()
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.UserId == userId, cancellationToken);

        return selection?.TenantId;
    }

    public async Task SetActiveTenantAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);
        var context = lease.Context;

        var hasActiveMembership = await context.Set<TenantMembershipEntry>()
            .AnyAsync(
                e => e.UserId == userId && e.TenantId == tenantId && e.Status == MembershipStatus.Active,
                cancellationToken);

        if (!hasActiveMembership)
        {
            throw new InvalidOperationException(
                $"User '{userId}' holds no active membership in tenant '{tenantId}'; the active-tenant selection was rejected.");
        }

        var existing = await context.Set<ActiveTenantEntry>()
            .SingleOrDefaultAsync(e => e.UserId == userId, cancellationToken);

        if (existing is null)
        {
            context.Set<ActiveTenantEntry>().Add(new ActiveTenantEntry { UserId = userId, TenantId = tenantId });
        }
        else
        {
            existing.TenantId = tenantId;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static TenantMembership ToContract(TenantMembershipEntry entry) =>
        new(entry.UserId, entry.TenantId, entry.Roles, entry.Status);
}
