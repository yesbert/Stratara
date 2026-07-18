using Stratara.Abstractions.Settings;

namespace Stratara.Testing;

/// <summary>
/// Thread-safe in-memory <see cref="ISettingStore"/> for tests — the drop-in double for the
/// EF-backed setting store, mirroring its contract semantics: exact-scope reads/writes,
/// <c>null</c>-value deletion, and the widened erasure-sweep semantics of
/// <see cref="DeleteScopeAsync"/> (a user scope erases the user's values across all tenants,
/// a tenant scope erases the tenant's values across all users, the global scope erases only
/// global values).
/// </summary>
public sealed class InMemorySettingStore : ISettingStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Name, string TenantId, string UserId), string> _values = [];

    /// <inheritdoc/>
    public Task<string?> GetOrNullAsync(string name, SettingScope scope, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_values.TryGetValue(Key(name, scope), out var value) ? value : null);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(SettingScope scope, CancellationToken cancellationToken = default)
    {
        var (tenantId, userId) = (scope.NormalizedTenantId, scope.NormalizedUserId);
        lock (_gate)
        {
            IReadOnlyDictionary<string, string> result = _values
                .Where(pair => pair.Key.TenantId == tenantId && pair.Key.UserId == userId)
                .ToDictionary(pair => pair.Key.Name, pair => pair.Value, StringComparer.Ordinal);
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task SetAsync(string name, string? value, SettingScope scope, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (value is null)
            {
                _values.Remove(Key(name, scope));
            }
            else
            {
                _values[Key(name, scope)] = value;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteScopeAsync(SettingScope scope, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var keys = _values.Keys.Where(key => scope switch
            {
                { UserId: not null, TenantId: not null } => key.UserId == scope.UserId && key.TenantId == scope.TenantId,
                { UserId: not null } => key.UserId == scope.UserId,
                { TenantId: not null } => key.TenantId == scope.TenantId,
                _ => key.TenantId.Length == 0 && key.UserId.Length == 0,
            }).ToList();

            foreach (var key in keys)
            {
                _values.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    private static (string Name, string TenantId, string UserId) Key(string name, SettingScope scope) =>
        (name, scope.NormalizedTenantId, scope.NormalizedUserId);

}
