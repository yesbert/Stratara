using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Stratara.Projections.Abstractions;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;

/// <summary>The deleted tenants of each projection, in the read store.</summary>
/// <typeparam name="TContext">A read context derived from the framework's read context, which declares the table.</typeparam>
internal sealed class ForgottenTenantStore<TContext>(IDbContextFactory<TContext> contextFactory) : IForgottenTenantStore
    where TContext : DbContext
{
    private const int MaxInsertAttempts = 3;

    /// <summary>The providers whose insert can skip a row that is already there: PostgreSQL and SQLite.</summary>
    private static readonly HashSet<string> ProvidersIgnoringAConflict = new(StringComparer.Ordinal)
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "Microsoft.EntityFrameworkCore.Sqlite",
    };

    /// <inheritdoc/>
    /// <remarks>
    /// Two deliveries of one projection may forget the same tenant at once — the two deletion facts of a tenant arrive
    /// in two bundles. On PostgreSQL and SQLite the rows are inserted in one statement that skips a row already there,
    /// so the statement does not fail and nothing is logged as an error. On any other provider the rows that are
    /// missing are inserted; when a concurrent writer inserts some of them first, the save fails as a whole, the
    /// provider logs the failed statement, and the rows still missing are inserted again, a bounded number of times. A
    /// conflict that outlasts the attempts propagates so the fact is applied again.
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
        if (context.Database.ProviderName is { } provider && ProvidersIgnoringAConflict.Contains(provider))
        {
            await context.Database.ExecuteSqlRawAsync(InsertIgnoringAConflict(context, wanted.Count), [projection, .. wanted.Cast<object>()], cancellationToken);
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

    /// <summary>
    /// The insert of one row per tenant, with the table and columns named as the context's model maps them. The
    /// projection is argument 0, the tenants follow it.
    /// </summary>
    private static string InsertIgnoringAConflict(TContext context, int tenants)
    {
        var entity = context.Model.FindEntityType(typeof(ForgottenTenant))
                     ?? throw new InvalidOperationException($"The model of {context.GetType().Name} has no {nameof(ForgottenTenant)}.");
        var tableName = entity.GetTableName()
                        ?? throw new InvalidOperationException($"{nameof(ForgottenTenant)} is not mapped to a table in {context.GetType().Name}.");
        var table = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
        var sql = context.GetService<ISqlGenerationHelper>();

        string Column(string property) => sql.DelimitIdentifier(entity.FindProperty(property)?.GetColumnName(table)
                                                                ?? throw new InvalidOperationException($"{nameof(ForgottenTenant)}.{property} is not mapped in {context.GetType().Name}."));

        var rows = string.Join(", ", Enumerable.Range(1, tenants).Select(tenant => $"({{0}}, {{{tenant}}})"));
        return $"INSERT INTO {sql.DelimitIdentifier(tableName, entity.GetSchema())} ({Column(nameof(ForgottenTenant.Projection))}, {Column(nameof(ForgottenTenant.TenantId))}) " +
               $"VALUES {rows} ON CONFLICT DO NOTHING";
    }
}
