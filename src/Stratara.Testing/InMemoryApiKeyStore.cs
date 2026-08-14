using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Multitenancy;

namespace Stratara.Testing;

/// <summary>
/// Thread-safe in-memory <see cref="IApiKeyStore"/> for tests — the drop-in double for the
/// EF-backed key store, mirroring its contract semantics: canonical raw keys, machine keys
/// materialized as memberships of their tenant, personal access tokens guarded by the bound user's
/// active membership, fail-closed validation, and the idempotent import path.
/// </summary>
/// <remarks>
/// The double keeps raw keys in memory instead of hashing them — the hash is a storage concern, and
/// nothing in the contract exposes it. Everything a test can observe (descriptors, memberships,
/// validation outcomes) behaves as it does in production.
/// </remarks>
/// <example>
/// Seed a known key and authenticate with it:
/// <code>
/// var keys = new InMemoryApiKeyStore();
/// var rawKey = ApiKeyFormat.CreateRawKey();
/// await keys.ImportAsync(new ApiKeyImportRequest(rawKey, TestTenants.Of("primary"), "ci", ["Deployer"]));
/// var descriptor = await keys.ValidateAsync(rawKey);
/// </code>
/// </example>
public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ApiKeyDescriptor> _keys = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>
    /// Creates the store, optionally sharing the membership store that machine keys materialize
    /// into and that personal access tokens are guarded against.
    /// </summary>
    /// <param name="memberships">
    /// The membership plane to write machine memberships into; a private
    /// <see cref="InMemoryTenantMembershipStore"/> is created when omitted.
    /// </param>
    /// <param name="clock">Clock backing issuance timestamps and expiry checks.</param>
    public InMemoryApiKeyStore(ITenantMembershipStore? memberships = null, TimeProvider? clock = null)
    {
        Memberships = memberships ?? new InMemoryTenantMembershipStore();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The membership plane this store materializes machine keys into — inspect it to assert that a
    /// key carries the roles it should.
    /// </summary>
    public ITenantMembershipStore Memberships { get; }

    /// <inheritdoc/>
    public async Task<IssuedApiKey> IssueAsync(
        ApiKeyIssueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        await EnsureIssuableAsync(request, cancellationToken);

        var rawKey = ApiKeyFormat.CreateRawKey();
        var descriptor = new ApiKeyDescriptor(
            Guid.CreateVersion7(), request.TenantId, request.UserId, request.Name,
            request.Roles.ToList(), _clock.GetUtcNow(), request.ExpiresAt);

        lock (_gate)
        {
            _keys[rawKey] = descriptor;
        }

        if (request.UserId is null)
        {
            await MaterializeMembershipAsync(descriptor, cancellationToken);
        }

        return new IssuedApiKey(rawKey, descriptor);
    }

    /// <inheritdoc/>
    public async Task<ApiKeyDescriptor> ImportAsync(
        ApiKeyImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        if (!ApiKeyFormat.IsWellFormed(request.RawKey))
        {
            throw new ArgumentException(
                $"The supplied key is not in the canonical format ('{ApiKeyFormat.Prefix}' followed by the " +
                "Base64Url encoding of 32 bytes). Generate it with ApiKeyFormat.CreateRawKey().",
                nameof(request));
        }

        ApiKeyDescriptor descriptor;
        lock (_gate)
        {
            if (_keys.TryGetValue(request.RawKey, out var known))
            {
                descriptor = AdoptExisting(known, request);
            }
            else
            {
                descriptor = new ApiKeyDescriptor(
                    Guid.CreateVersion7(), request.TenantId, null, request.Name,
                    request.Roles.ToList(), _clock.GetUtcNow(), request.ExpiresAt);
                _keys[request.RawKey] = descriptor;
            }
        }

        await MaterializeMembershipAsync(descriptor, cancellationToken);
        return descriptor;
    }

    /// <inheritdoc/>
    public Task<ApiKeyDescriptor?> ValidateAsync(string rawKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return Task.FromResult<ApiKeyDescriptor?>(null);
        }

        lock (_gate)
        {
            if (!_keys.TryGetValue(rawKey, out var descriptor)
                || descriptor.RevokedAt is not null
                || (descriptor.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow()))
            {
                return Task.FromResult<ApiKeyDescriptor?>(null);
            }

            return Task.FromResult<ApiKeyDescriptor?>(descriptor);
        }
    }

    /// <inheritdoc/>
    public async Task RevokeAsync(Guid apiKeyId, CancellationToken cancellationToken = default)
    {
        ApiKeyDescriptor revoked;
        lock (_gate)
        {
            var match = _keys.FirstOrDefault(pair => pair.Value.Id == apiKeyId);
            if (match.Key is null || match.Value.RevokedAt is not null)
            {
                return;
            }

            revoked = match.Value with { RevokedAt = _clock.GetUtcNow() };
            _keys[match.Key] = revoked;
        }

        if (revoked.UserId is null)
        {
            await Memberships.RemoveMembershipAsync(revoked.Id, revoked.TenantId, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ApiKeyDescriptor>> GetForTenantAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ApiKeyDescriptor> result =
                _keys.Values.Where(descriptor => descriptor.TenantId == tenantId).ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public async Task RemoveAllForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        List<ApiKeyDescriptor> removed;
        lock (_gate)
        {
            removed = _keys.Values.Where(descriptor => descriptor.TenantId == tenantId).ToList();
            foreach (var rawKey in _keys
                         .Where(pair => pair.Value.TenantId == tenantId)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _keys.Remove(rawKey);
            }
        }

        foreach (var descriptor in removed.Where(descriptor => descriptor.UserId is null))
        {
            await Memberships.RemoveMembershipAsync(descriptor.Id, descriptor.TenantId, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public Task RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var rawKey in _keys
                         .Where(pair => pair.Value.UserId == userId)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _keys.Remove(rawKey);
            }
        }

        return Task.CompletedTask;
    }

    private ApiKeyDescriptor AdoptExisting(ApiKeyDescriptor existing, ApiKeyImportRequest request)
    {
        if (existing.UserId is not null)
        {
            throw new InvalidOperationException(
                $"The supplied key value already exists as personal access token '{existing.Id}'; " +
                "import a fresh machine key instead.");
        }

        if (existing.TenantId != request.TenantId)
        {
            throw new InvalidOperationException(
                $"The supplied key value is already stored for tenant '{existing.TenantId}' and cannot be " +
                $"re-bound to tenant '{request.TenantId}'.");
        }

        if (existing.RevokedAt is not null)
        {
            throw new InvalidOperationException(
                $"The supplied key value belongs to revoked key '{existing.Id}'; a revoked key is never " +
                "reinstated. Import a new key value.");
        }

        if (existing.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow())
        {
            throw new InvalidOperationException(
                $"The supplied key value belongs to expired key '{existing.Id}'; an expired key is never " +
                "extended. Import a new key value.");
        }

        return existing;
    }

    private async Task EnsureIssuableAsync(ApiKeyIssueRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId is not { } userId)
        {
            return;
        }

        if (request.Roles.Count > 0)
        {
            throw new InvalidOperationException(
                "Personal access tokens carry no roles of their own — they act as the bound user. " +
                "Issue a machine key (no UserId) to grant key-scoped roles.");
        }

        var membership = await Memberships.GetMembershipAsync(userId, request.TenantId, cancellationToken);
        if (membership is not { Status: MembershipStatus.Active })
        {
            throw new InvalidOperationException(
                $"User '{userId}' holds no active membership in tenant '{request.TenantId}'; the personal access token was not issued.");
        }
    }

    private async Task MaterializeMembershipAsync(ApiKeyDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (await Memberships.GetMembershipAsync(descriptor.Id, descriptor.TenantId, cancellationToken) is not null)
        {
            return;
        }

        await Memberships.SetMembershipAsync(
            new TenantMembership(descriptor.Id, descriptor.TenantId, descriptor.Roles.ToList()),
            cancellationToken);
    }
}
