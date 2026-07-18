using Stratara.Abstractions.Multitenancy;

namespace Stratara.Testing;

/// <summary>
/// Thread-safe in-memory <see cref="ITenantMembershipStore"/> for tests — the drop-in double for
/// the EF-backed membership store, mirroring its contract semantics: upsert on
/// <see cref="SetMembershipAsync"/>, erasure sweeps per user and per tenant, and the
/// membership-guarded active-tenant selection (selecting a tenant without an active membership
/// throws, removals clear a now-dangling selection).
/// </summary>
public sealed class InMemoryTenantMembershipStore : ITenantMembershipStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid UserId, Guid TenantId), TenantMembership> _memberships = [];
    private readonly Dictionary<Guid, Guid> _activeTenants = [];

    /// <inheritdoc/>
    public Task<IReadOnlyList<TenantMembership>> GetMembershipsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TenantMembership> result =
                _memberships.Values.Where(m => m.UserId == userId).ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<TenantMembership?> GetMembershipAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_memberships.GetValueOrDefault((userId, tenantId)));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TenantMembership>> GetMembersAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TenantMembership> result =
                _memberships.Values.Where(m => m.TenantId == tenantId).ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task SetMembershipAsync(TenantMembership membership, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(membership);
        lock (_gate)
        {
            _memberships[(membership.UserId, membership.TenantId)] = membership;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveMembershipAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _memberships.Remove((userId, tenantId));
            if (_activeTenants.GetValueOrDefault(userId) == tenantId)
            {
                _activeTenants.Remove(userId);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var key in _memberships.Keys.Where(k => k.UserId == userId).ToList())
            {
                _memberships.Remove(key);
            }

            _activeTenants.Remove(userId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveAllMembersAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var key in _memberships.Keys.Where(k => k.TenantId == tenantId).ToList())
            {
                _memberships.Remove(key);
            }

            foreach (var userId in _activeTenants
                         .Where(pair => pair.Value == tenantId)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _activeTenants.Remove(userId);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Guid?> GetActiveTenantAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<Guid?>(
                _activeTenants.TryGetValue(userId, out var tenantId) ? tenantId : null);
        }
    }

    /// <inheritdoc/>
    public Task SetActiveTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var membership = _memberships.GetValueOrDefault((userId, tenantId));
            if (membership is not { Status: MembershipStatus.Active })
            {
                throw new InvalidOperationException(
                    $"User '{userId}' holds no active membership in tenant '{tenantId}'; the active-tenant selection was rejected.");
            }

            _activeTenants[userId] = tenantId;
        }

        return Task.CompletedTask;
    }
}
