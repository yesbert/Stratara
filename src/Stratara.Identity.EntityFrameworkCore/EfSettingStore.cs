using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.Settings;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="ISettingStore"/> against any DbContext whose model
/// includes the identity-directory tables. Scope dimensions are stored as empty strings when
/// unset (see <see cref="SettingEntry"/>).
/// </summary>
internal sealed class EfSettingStore<TContext>(TContext context) : ISettingStore
    where TContext : DbContext
{
    public async Task<string?> GetOrNullAsync(
        string name, SettingScope scope, CancellationToken cancellationToken = default)
    {
        var (tenantId, userId) = (scope.NormalizedTenantId, scope.NormalizedUserId);
        var entry = await context.Set<SettingEntry>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.Name == name && e.TenantId == tenantId && e.UserId == userId,
                cancellationToken);

        return entry?.Value;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        SettingScope scope, CancellationToken cancellationToken = default)
    {
        var (tenantId, userId) = (scope.NormalizedTenantId, scope.NormalizedUserId);
        var entries = await context.Set<SettingEntry>()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.UserId == userId)
            .ToListAsync(cancellationToken);

        return entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
    }

    public async Task SetAsync(
        string name, string? value, SettingScope scope, CancellationToken cancellationToken = default)
    {
        var (tenantId, userId) = (scope.NormalizedTenantId, scope.NormalizedUserId);

        if (value is null)
        {
            await context.Set<SettingEntry>()
                .Where(e => e.Name == name && e.TenantId == tenantId && e.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            return;
        }

        var existing = await context.Set<SettingEntry>()
            .SingleOrDefaultAsync(
                e => e.Name == name && e.TenantId == tenantId && e.UserId == userId,
                cancellationToken);

        if (existing is null)
        {
            context.Set<SettingEntry>().Add(new SettingEntry
            {
                Name = name,
                TenantId = tenantId,
                UserId = userId,
                Value = value,
            });
        }
        else
        {
            existing.Value = value;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteScopeAsync(SettingScope scope, CancellationToken cancellationToken = default)
    {
        var query = context.Set<SettingEntry>().AsQueryable();

        if (scope.UserId is { } userId)
        {
            query = query.Where(e => e.UserId == userId);
        }

        if (scope.TenantId is { } tenantId)
        {
            query = query.Where(e => e.TenantId == tenantId);
        }

        if (scope.UserId is null && scope.TenantId is null)
        {
            query = query.Where(e => e.TenantId == string.Empty && e.UserId == string.Empty);
        }

        await query.ExecuteDeleteAsync(cancellationToken);
    }
}
