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
    private const int MaxRoleChangeAttempts = 5;

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

        if (membership.Status != MembershipStatus.Active)
        {
            await context.DeleteAndForgetAsync<ActiveTenantEntry>(e => e.UserId == membership.UserId && e.TenantId == membership.TenantId, cancellationToken);
        }
    }

    public Task<TenantMembership?> AddRolesAsync(
        Guid userId, Guid tenantId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return ChangeRolesAsync(userId, tenantId, membership => membership.WithRolesAdded(roles), cancellationToken);
    }

    public Task<TenantMembership?> RemoveRolesAsync(
        Guid userId, Guid tenantId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return ChangeRolesAsync(userId, tenantId, membership => membership.WithRolesRemoved(roles), cancellationToken);
    }

    private async Task<TenantMembership?> ChangeRolesAsync(
        Guid userId, Guid tenantId, Func<TenantMembership, TenantMembership> change, CancellationToken cancellationToken)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);
        var context = lease.Context;

        List<string>? previous = null;
        var sawConcurrentChange = false;
        for (var attempt = 0; attempt < MaxRoleChangeAttempts; attempt++)
        {
            var entry = await context.Set<TenantMembershipEntry>()
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.UserId == userId && e.TenantId == tenantId, cancellationToken);
            if (entry is null)
            {
                return null;
            }

            var membership = ToContract(entry);
            var changed = change(membership);
            if (ReferenceEquals(changed, membership))
            {
                return membership;
            }

            var read = entry.Roles;
            sawConcurrentChange |= previous is not null && !previous.SequenceEqual(read, StringComparer.Ordinal);
            previous = read;
            var next = changed.Roles.ToList();
            var updated = await context.Set<TenantMembershipEntry>()
                .Where(e => e.UserId == userId && e.TenantId == tenantId && e.Roles == read)
                .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.Roles, next), cancellationToken);
            if (updated == 1)
            {
                SyncTrackedRoles(context, userId, tenantId, next);
                return changed;
            }
        }

        throw new InvalidOperationException(sawConcurrentChange
            ? $"The roles of user '{userId}' in tenant '{tenantId}' were changed concurrently on each of " +
              $"{MaxRoleChangeAttempts} attempts; the role change was not applied."
            : $"The stored roles of user '{userId}' in tenant '{tenantId}' are not in the form this store writes, so " +
              "the role change could not be applied safely. Write the role set you want with SetMembershipAsync instead; " +
              "that write stores the roles in this store's form, and later role changes work again.");
    }

    private static void SyncTrackedRoles(TContext context, Guid userId, Guid tenantId, List<string> roles)
    {
        var autoDetect = context.ChangeTracker.AutoDetectChangesEnabled;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var tracked = context.ChangeTracker.Entries<TenantMembershipEntry>()
                .FirstOrDefault(e => e.Entity.UserId == userId && e.Entity.TenantId == tenantId);
            if (tracked is null)
            {
                return;
            }

            tracked.DetectChanges();
            var property = tracked.Property(e => e.Roles);
            if (property.IsModified)
            {
                return;
            }

            property.CurrentValue = roles.ToList();
            property.OriginalValue = roles.ToList();
        }
        finally
        {
            context.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    public async Task RemoveMembershipAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);
        var context = lease.Context;

        await context.DeleteAndForgetAsync<TenantMembershipEntry>(e => e.UserId == userId && e.TenantId == tenantId, cancellationToken);

        await context.DeleteAndForgetAsync<ActiveTenantEntry>(e => e.UserId == userId && e.TenantId == tenantId, cancellationToken);
    }

    public async Task RemoveAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);
        var context = lease.Context;

        await context.DeleteAndForgetAsync<TenantMembershipEntry>(e => e.UserId == userId, cancellationToken);

        await context.DeleteAndForgetAsync<ActiveTenantEntry>(e => e.UserId == userId, cancellationToken);
    }

    public async Task RemoveAllMembersAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var lease = await contextSource.LeaseAsync(cancellationToken);
        var context = lease.Context;

        await context.DeleteAndForgetAsync<TenantMembershipEntry>(e => e.TenantId == tenantId, cancellationToken);

        await context.DeleteAndForgetAsync<ActiveTenantEntry>(e => e.TenantId == tenantId, cancellationToken);
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
