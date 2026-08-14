using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <para>
/// Machine keys are materialized into the membership plane: issuance writes a
/// <c>tenant_membership</c> row for the key id carrying the key's roles, so role checks,
/// permission resolution, and cross-tenant authorization treat the key exactly like any other
/// actor — no parallel authorization path. Revocation and the erasure sweeps remove those rows
/// again. Personal access tokens write no membership; they act as their bound user.
/// </para>
/// <para>
/// <see cref="ImportAsync"/> is the same write path for a key the caller already holds. It accepts
/// only values in the canonical <see cref="ApiKeyFormat"/>, which is what keeps the unsalted digest
/// defensible for imported keys too.
/// </para>
/// </remarks>
internal sealed class EfApiKeyStore<TContext>(
    TContext context,
    TimeProvider? clock = null,
    ILogger<EfApiKeyStore<TContext>>? logger = null) : IApiKeyStore
    where TContext : DbContext
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger<EfApiKeyStore<TContext>>.Instance;

    public async Task<IssuedApiKey> IssueAsync(
        ApiKeyIssueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        await EnsureIssuableAsync(request, cancellationToken);

        var rawKey = ApiKeyFormat.CreateRawKey();
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
            context.Set<TenantMembershipEntry>().Add(MachineMembershipFor(entry));
        }

        await context.SaveChangesAsync(cancellationToken);
        return new IssuedApiKey(rawKey, ToDescriptor(entry));
    }

    public async Task<ApiKeyDescriptor> ImportAsync(
        ApiKeyImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        if (!ApiKeyFormat.IsWellFormed(request.RawKey))
        {
            throw new ArgumentException(
                $"The supplied key is not in the canonical format ('{ApiKeyFormat.Prefix}' followed by the " +
                "Base64Url encoding of 32 bytes). Generate it with ApiKeyFormat.CreateRawKey(); a hand-picked " +
                "value would undermine the unsalted digest the store relies on.",
                nameof(request));
        }

        var hashedKey = Hash(request.RawKey);
        if (await FindByHashAsync(hashedKey, cancellationToken) is { } known)
        {
            return await AdoptExistingAsync(known, request, cancellationToken);
        }

        var entry = new ApiKeyEntry
        {
            Id = Guid.CreateVersion7(),
            HashedKey = hashedKey,
            TenantId = request.TenantId,
            UserId = null,
            Name = request.Name,
            Roles = request.Roles.ToList(),
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = request.ExpiresAt,
        };
        var membership = MachineMembershipFor(entry);

        context.Set<ApiKeyEntry>().Add(entry);
        context.Set<TenantMembershipEntry>().Add(membership);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent boot-time seeding of the same key: the unique hash index rejected the
            // second writer. Drop this attempt and adopt the row the winner wrote.
            context.Entry(entry).State = EntityState.Detached;
            context.Entry(membership).State = EntityState.Detached;

            if (await FindByHashAsync(hashedKey, cancellationToken) is not { } raced)
            {
                throw;
            }

            return await AdoptExistingAsync(raced, request, cancellationToken);
        }

        ApiKeyStoreLog.KeyImported(_logger, entry.Id, entry.TenantId);
        return ToDescriptor(entry);
    }

    public async Task<ApiKeyDescriptor?> ValidateAsync(
        string rawKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return null;
        }

        var entry = await FindByHashAsync(Hash(rawKey), cancellationToken);

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

    private Task<ApiKeyEntry?> FindByHashAsync(string hashedKey, CancellationToken cancellationToken) =>
        context.Set<ApiKeyEntry>()
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.HashedKey == hashedKey, cancellationToken);

    private async Task<ApiKeyDescriptor> AdoptExistingAsync(
        ApiKeyEntry existing, ApiKeyImportRequest request, CancellationToken cancellationToken)
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

        if (DifferingParameter(existing, request) is { } differing)
        {
            ApiKeyStoreLog.KeyImportDiffers(_logger, existing.Id, existing.TenantId, differing);
        }
        else
        {
            ApiKeyStoreLog.KeyImportNoOp(_logger, existing.Id, existing.TenantId);
        }

        await EnsureMachineMembershipAsync(existing, cancellationToken);
        return ToDescriptor(existing);
    }

    private async Task EnsureMachineMembershipAsync(ApiKeyEntry entry, CancellationToken cancellationToken)
    {
        var exists = await context.Set<TenantMembershipEntry>()
            .AnyAsync(e => e.UserId == entry.Id && e.TenantId == entry.TenantId, cancellationToken);

        if (exists)
        {
            return;
        }

        // The row is gone from the database, but the sweeps delete without going through the change
        // tracker — a stale tracked copy would block the re-insert. Drop it and write a fresh row.
        var stale = context.Set<TenantMembershipEntry>().Local
            .FirstOrDefault(e => e.UserId == entry.Id && e.TenantId == entry.TenantId);

        if (stale is not null)
        {
            context.Entry(stale).State = EntityState.Detached;
        }

        context.Set<TenantMembershipEntry>().Add(MachineMembershipFor(entry));
        await context.SaveChangesAsync(cancellationToken);
    }

    private static TenantMembershipEntry MachineMembershipFor(ApiKeyEntry entry) =>
        new()
        {
            UserId = entry.Id,
            TenantId = entry.TenantId,
            Roles = entry.Roles.ToList(),
            Status = MembershipStatus.Active,
        };

    private static string? DifferingParameter(ApiKeyEntry existing, ApiKeyImportRequest request)
    {
        if (!string.Equals(existing.Name, request.Name, StringComparison.Ordinal))
        {
            return "name";
        }

        if (!existing.Roles.Order(StringComparer.Ordinal)
                .SequenceEqual(request.Roles.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return "role set";
        }

        return existing.ExpiresAt == request.ExpiresAt ? null : "expiry";
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
