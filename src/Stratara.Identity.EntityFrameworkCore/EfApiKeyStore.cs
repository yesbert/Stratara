using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Multitenancy;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IApiKeyStore"/>. Raw keys are
/// <c>stk_</c>-prefixed Base64Url strings over 32 CSPRNG bytes (the prefix helps secret
/// scanners spot leaked keys); storage holds only the lowercase-hex SHA-256 digest — unsalted
/// by design, because the key itself carries 256 bits of entropy, which makes the digest the
/// constant-time-safe lookup index.
/// </summary>
/// <remarks>
/// Machine keys are materialized into the membership plane: issuance writes a
/// <c>tenant_membership</c> row for the key id carrying the key's roles, so role checks,
/// permission resolution, and cross-tenant authorization treat the key exactly like any other
/// actor — no parallel authorization path. Revocation and the erasure sweeps remove those rows
/// again. Personal access tokens write no membership; they act as their bound user.
/// </remarks>
internal sealed class EfApiKeyStore<TContext>(TContext context, TimeProvider? clock = null) : IApiKeyStore
    where TContext : DbContext
{
    private const string KeyPrefix = "stk_";
    private const int RandomByteCount = 32;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IssuedApiKey> IssueAsync(
        ApiKeyIssueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        await EnsureIssuableAsync(request, cancellationToken);

        var rawKey = KeyPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomByteCount));
        var entry = new ApiKeyEntry
        {
            Id = Guid.CreateVersion7(),
            HashedKey = Hash(rawKey),
            TenantId = request.TenantId,
            UserId = request.UserId,
            Name = request.Name,
            Roles = request.Roles.ToList(),
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = request.ExpiresAt,
        };

        context.Set<ApiKeyEntry>().Add(entry);

        if (request.UserId is null)
        {
            context.Set<TenantMembershipEntry>().Add(new TenantMembershipEntry
            {
                UserId = entry.Id,
                TenantId = entry.TenantId,
                Roles = entry.Roles.ToList(),
                Status = MembershipStatus.Active,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
        return new IssuedApiKey(rawKey, ToDescriptor(entry));
    }

    public async Task<ApiKeyDescriptor?> ValidateAsync(
        string rawKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return null;
        }

        var hashed = Hash(rawKey);
        var entry = await context.Set<ApiKeyEntry>()
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.HashedKey == hashed, cancellationToken);

        if (entry is null
            || entry.RevokedAt is not null
            || (entry.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow()))
        {
            return null;
        }

        return ToDescriptor(entry);
    }

    public async Task RevokeAsync(Guid apiKeyId, CancellationToken cancellationToken = default)
    {
        var entry = await context.Set<ApiKeyEntry>()
            .SingleOrDefaultAsync(e => e.Id == apiKeyId, cancellationToken);

        if (entry is null || entry.RevokedAt is not null)
        {
            return;
        }

        entry.RevokedAt = _clock.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);

        if (entry.UserId is null)
        {
            await RemoveMachineMembershipsAsync([entry.Id], cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ApiKeyDescriptor>> GetForTenantAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        var entries = await context.Set<ApiKeyEntry>()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        return entries.Select(ToDescriptor).ToList();
    }

    public async Task RemoveAllForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var machineKeyIds = await context.Set<ApiKeyEntry>()
            .Where(e => e.TenantId == tenantId && e.UserId == null)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        await context.Set<ApiKeyEntry>()
            .Where(e => e.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

        await RemoveMachineMembershipsAsync(machineKeyIds, cancellationToken);
    }

    public async Task RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await context.Set<ApiKeyEntry>()
            .Where(e => e.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
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

        var hasActiveMembership = await context.Set<TenantMembershipEntry>()
            .AnyAsync(
                e => e.UserId == userId && e.TenantId == request.TenantId && e.Status == MembershipStatus.Active,
                cancellationToken);

        if (!hasActiveMembership)
        {
            throw new InvalidOperationException(
                $"User '{userId}' holds no active membership in tenant '{request.TenantId}'; the personal access token was not issued.");
        }
    }

    private async Task RemoveMachineMembershipsAsync(
        IReadOnlyCollection<Guid> machineKeyIds, CancellationToken cancellationToken)
    {
        if (machineKeyIds.Count == 0)
        {
            return;
        }

        await context.Set<TenantMembershipEntry>()
            .Where(e => machineKeyIds.Contains(e.UserId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static string Hash(string rawKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

    private static ApiKeyDescriptor ToDescriptor(ApiKeyEntry entry) =>
        new(entry.Id, entry.TenantId, entry.UserId, entry.Name, entry.Roles,
            entry.CreatedAt, entry.ExpiresAt, entry.RevokedAt);
}
