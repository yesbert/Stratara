using Microsoft.EntityFrameworkCore;
using Stratara.EntityFrameworkCore;
using Stratara.Projections.Abstractions;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;

/// <summary>The deleted tenants of each projection, in the read store.</summary>
/// <typeparam name="TContext">A read context derived from the framework's read context, which declares the table.</typeparam>
internal sealed class ForgottenTenantStore<TContext>(IDbContextFactory<TContext> contextFactory) : IForgottenTenantStore
    where TContext : DbContext
{
    private const int MaxInsertAttempts = 3;

    /// <inheritdoc/>
    /// <remarks>
    /// Two deliveries of one projection may forget the same tenant at once — the two deletion facts of a tenant arrive
    /// in two bundles. On PostgreSQL and SQLite the rows are inserted in one statement that skips a row already there,
    /// so the statement does not fail and nothing is logged as an error; the tenants are inserted in order, so two
    /// statements that share some of them cannot wait on each other, and the statement runs under the context's
    /// execution strategy, since running it again changes nothing. On any other provider the rows that are missing
    /// are inserted; when a concurrent writer inserts some of them first, the save fails as a whole, EF Core logs the
    /// failed statement, and the rows still missing are inserted again, a bounded number of times. A conflict that
    /// outlasts the attempts propagates so the fact is applied again.
    /// </remarks>
    public async Task ForgetAsync(string projection, IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(tenantIds);

        var wanted = tenantIds.Distinct().Order().ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = wanted.Select(tenantId => new ForgottenTenant { Projection = projection, TenantId = tenantId }).ToList();
        if (ConflictIgnoringInsert.CanInsert(context, rows))
        {
            await ConflictIgnoringInsert.InsertAsync(context, rows, cancellationToken);
            return;
        }

        var attempt = 0;
        while (true)
        {
            attempt++;
            var missing = await MissingAsync(context, projection, wanted, cancellationToken);
            if (missing.Count == 0)
            {
                return;
            }

            context.Set<ForgottenTenant>().AddRange(missing.Select(tenantId => new ForgottenTenant { Projection = projection, TenantId = tenantId }));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < MaxInsertAttempts)
            {
                context.ChangeTracker.Clear();
            }
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

    private static async Task<List<Guid>> MissingAsync(TContext context, string projection, List<Guid> wanted, CancellationToken cancellationToken)
    {
        var present = await context.Set<ForgottenTenant>().AsNoTracking()
            .Where(forgotten => forgotten.Projection == projection && wanted.Contains(forgotten.TenantId))
            .Select(forgotten => forgotten.TenantId)
            .ToListAsync(cancellationToken);
        return wanted.Except(present).ToList();
    }
}
