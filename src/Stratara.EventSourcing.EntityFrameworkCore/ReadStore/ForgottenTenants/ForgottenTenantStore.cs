using Microsoft.EntityFrameworkCore;
using Stratara.Projections.Abstractions;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;

/// <summary>The deleted tenants of each projection, in the read store.</summary>
/// <typeparam name="TContext">A read context derived from the framework's read context, which declares the table.</typeparam>
internal sealed class ForgottenTenantStore<TContext>(IDbContextFactory<TContext> contextFactory) : IForgottenTenantStore
    where TContext : DbContext
{
    /// <inheritdoc/>
    /// <remarks>
    /// Inserts the rows that are missing. A writer whose insert loses to another's finds the rows present
    /// and writes nothing; a conflict that leaves a row missing propagates, so the fact is applied again.
    /// </remarks>
    public async Task ForgetAsync(string projection, IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(tenantIds);

        var wanted = tenantIds.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var missing = await MissingAsync(context, projection, wanted, cancellationToken);
        if (missing.Count == 0)
        {
            return;
        }

        context.Set<ForgottenTenant>().AddRange(missing.Select(tenantId => new ForgottenTenant { Projection = projection, TenantId = tenantId }));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if ((await MissingAsync(context, projection, wanted, cancellationToken)).Count == 0)
            {
                return;
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> HasForgottenAsync(string projection, Guid tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Set<ForgottenTenant>().AsNoTracking()
            .AnyAsync(forgotten => forgotten.Projection == projection && forgotten.TenantId == tenantId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(string projection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<ForgottenTenant>().Where(forgotten => forgotten.Projection == projection).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<ForgottenTenant>().ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<List<Guid>> MissingAsync(TContext context, string projection, List<Guid> wanted, CancellationToken cancellationToken)
    {
        var present = await context.Set<ForgottenTenant>().AsNoTracking()
            .Where(forgotten => forgotten.Projection == projection && wanted.Contains(forgotten.TenantId))
            .Select(forgotten => forgotten.TenantId)
            .ToListAsync(cancellationToken);
        return wanted.Except(present).ToList();
    }
}
